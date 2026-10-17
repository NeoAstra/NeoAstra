from __future__ import annotations

import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


class WindowsDownloadLifetimeTests(unittest.TestCase):
    def test_pending_download_survives_shutdown_until_deferred_completion(self) -> None:
        compiler = shutil.which("clang++")
        if compiler is None:
            self.skipTest("Clang is required for the source-backed Windows lifetime fixture")

        # Compile the production context, completion, construction, and retain
        # guard without the Windows SDK. Only COM and the download/UI teardown
        # are stand-ins: this is not a WebView2 or real app-detach runtime test.
        backend = (ROOT / "native/src/windows/windows_backend.cpp").read_text(encoding="utf-8")
        internal = (ROOT / "native/src/common/native_internal.hpp").read_text(encoding="utf-8")
        owners = internal[internal.index("struct neo_ref_counted {"):internal.index("struct neoastra_error final")]
        context = backend[backend.index("struct download_decision_context {"):backend.index("struct navigation_decision_context {")]
        construct = next(line.strip() for line in backend.splitlines() if "auto context=std::make_unique<download_decision_context>" in line)
        fixture = r"""
#include "neoastra.h"
#include <atomic>
#include <cassert>
#include <cstdint>
#include <limits>
#include <memory>
#include <string>
""" + owners + r"""
struct observations {
    int downloads_destroyed{};
    int args_destroyed{};
    int deferrals_destroyed{};
    int canceled{};
    int completed{};
};
struct neoastra_view final : neo_ref_counted { };
struct neoastra_download final : neo_ref_counted {
    observations& observed;
    neoastra_view_t* view{};
    std::atomic<neoastra_download_state_t> state{NEOASTRA_DOWNLOAD_REQUESTED};
    std::string destination_path;
    bool lifecycle_released{};
    explicit neoastra_download(observations& value, neoastra_view_t* owner) : observed(value), view(owner) { }
    ~neoastra_download() override { ++observed.downloads_destroyed; }
    void release_lifecycle() noexcept {
        if (lifecycle_released) return;
        lifecycle_released = true;
        view = nullptr;
        state.store(NEOASTRA_DOWNLOAD_CANCELED);
        release();
    }
};
struct ICoreWebView2DownloadStartingEventArgs {
    observations& observed;
    explicit ICoreWebView2DownloadStartingEventArgs(observations& value) : observed(value) { }
    ~ICoreWebView2DownloadStartingEventArgs() { assert(observed.downloads_destroyed == 0); ++observed.args_destroyed; }
    void put_Cancel(int) { assert(observed.downloads_destroyed == 0); ++observed.canceled; }
    void put_Handled(int) { }
    void put_ResultFilePath(const wchar_t*) { }
};
struct ICoreWebView2Deferral {
    observations& observed;
    explicit ICoreWebView2Deferral(observations& value) : observed(value) { }
    ~ICoreWebView2Deferral() { assert(observed.downloads_destroyed == 0); ++observed.deferrals_destroyed; }
    void Complete() { assert(observed.downloads_destroyed == 0); ++observed.completed; }
};
template<class T> using ComPtr = std::shared_ptr<T>;
constexpr int TRUE = 1;
std::string neo_string(neoastra_string_view_t text) {
    return {reinterpret_cast<const char*>(text.data), static_cast<size_t>(text.length)};
}
std::wstring widen(const std::string& text) { return {text.begin(), text.end()}; }
bool register_download_events(neoastra_download_t*) noexcept { assert(false); return false; }
void neo_download_emit(neoastra_download_t*, neoastra_event_type_t) noexcept { assert(false); }
""" + context + r"""
int main() {
    observations observed;
    neoastra_view view;
    auto* download = new neoastra_download(observed, &view);
    auto args = std::make_shared<ICoreWebView2DownloadStartingEventArgs>(observed);
    auto deferral = std::make_shared<ICoreWebView2Deferral>(observed);
""" + construct + r"""
    auto* pending = context.release();
    args.reset();
    deferral.reset();
    // The client deferred the decision, but never retained the download. Model
    // shutdown visiting the newer phase-0 download before abandoning the view's
    // deferred decision: its lifecycle reference is gone and its weak view clear.
    download->release_lifecycle();
    assert(observed.downloads_destroyed == 0);
    neoastra_decision_response_t response{};
    response.action = NEOASTRA_DECISION_CANCEL;
    download_decided(pending, &response);
    assert(observed.canceled == 1 && observed.completed == 1);
    assert(observed.args_destroyed == 1 && observed.deferrals_destroyed == 1);
    assert(observed.downloads_destroyed == 1);
}
"""
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            source = directory / "windows_download_lifetime.cpp"
            executable = directory / "windows_download_lifetime"
            source.write_text(fixture, encoding="utf-8")
            compiled = subprocess.run(
                [compiler, "-std=c++20", "-I", str(ROOT / "native/include"), str(source), "-o", str(executable)],
                cwd=directory, capture_output=True, text=True, timeout=60,
            )
            self.assertEqual(0, compiled.returncode, compiled.stdout + compiled.stderr)
            completed = subprocess.run([str(executable)], cwd=directory, capture_output=True, text=True, timeout=10)
            self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)


if __name__ == "__main__":
    unittest.main()
