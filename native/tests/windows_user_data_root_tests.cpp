// Tests of the user-data root paths of the Windows backend. Every test works inside one folder that it creates under the temporary
// folder, with a unique name, and removes nothing else: a deletion here only ever reaches a folder that a test created.

#include "../src/windows/user_data_root_paths.hpp"

#include <knownfolders.h>
#include <shlobj.h>

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iterator>
#include <string>

using namespace neo_windows_roots;
using namespace std::chrono_literals;
namespace fs = std::filesystem;

namespace {

// The unique folder of this run, under the temporary folder. The tests never touch anything outside it.
std::wstring make_base() {
    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    const auto base = fs::path(temp) / (L"neoastra-root-tests-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
                                        std::to_wstring(GetTickCount64()));
    fs::create_directories(base);
    return base.wstring();
}

std::wstring resolved_of(const std::wstring& path) {
    std::wstring resolved;
    DWORD error{};
    const bool ok = resolve_folder(path, resolved, error);
    if (!ok) std::fprintf(stderr, "resolve_folder(%ls) failed with %lu\n", path.c_str(), static_cast<unsigned long>(error));
    assert(ok);
    return resolved;
}

std::wstring key_of_path(const std::wstring& path) { return key_of(resolved_of(path)); }

void write_file(const std::wstring& path, const char* text) {
    std::ofstream(fs::path(path), std::ios::binary) << text;
}

std::string read_file(const std::wstring& path) {
    std::ifstream stream(fs::path(path), std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
}

// A never-canceled deletion wait, short enough for the tests that expect a folder to stay in use.
const std::function<bool()> never_canceled = [] { return false; };

void test_relative_paths_follow_the_executable(const std::wstring& base) {
    wchar_t executable[MAX_PATH]{};
    const auto length = GetModuleFileNameW(nullptr, executable, MAX_PATH);
    assert(length && length < MAX_PATH);
    const auto expected = fs::path(executable).parent_path() / L"relative-store";
    const auto previous = fs::current_path();
    fs::current_path(base);
    assert(key_of_path(L"relative-store") == key_of_path(expected.wstring()));
    fs::current_path(previous);
}

void test_spellings_of_one_folder_have_one_key(const std::wstring& base) {
    const auto real = base + L"\\spellings";
    fs::create_directories(real + L"\\child");
    const auto key = key_of_path(real + L"\\child");

    // Case: the keys are lowercase, and the final path names an existing folder as the file system does.
    assert(key_of_path(real + L"\\CHILD") == key);

    // A junction names the same folder, and so does its path below the junction.
    const auto junction = base + L"\\spellings-junction";
    const auto command = L"cmd /c mklink /J \"" + junction + L"\" \"" + real + L"\" > nul";
    assert(_wsystem(command.c_str()) == 0);
    assert(key_of_path(junction + L"\\child") == key);
    assert(key_of_path(junction) == key_of_path(real));

    // A short name of the folder, where the volume has one.
    wchar_t shortened[MAX_PATH]{};
    const auto length = GetShortPathNameW(real.c_str(), shortened, MAX_PATH);
    if (length > 0 && std::wstring(shortened) != real) {
        assert(key_of_path(std::wstring(shortened) + L"\\child") == key);
    } else {
        std::printf("skipped: the volume has no short name for %ls\n", real.c_str());
    }
}

void test_missing_parts_are_appended_to_the_existing_part(const std::wstring& base) {
    const auto real = base + L"\\missing-real";
    fs::create_directories(real);
    const auto junction = base + L"\\missing-junction";
    const auto command = L"cmd /c mklink /J \"" + junction + L"\" \"" + real + L"\" > nul";
    assert(_wsystem(command.c_str()) == 0);
    // The folder below the junction does not exist yet: the existing part is the junction, which names the real folder.
    assert(key_of_path(junction + L"\\not-yet\\deeper") == key_of_path(real + L"\\not-yet\\deeper"));
}

void test_device_paths_are_not_resolved() {
    std::wstring resolved;
    DWORD error{};
    assert(!resolve_folder(L"\\\\.\\C:\\anything", resolved, error));
    assert(error == ERROR_INVALID_NAME);
}

void test_protected_folders_are_refused() {
    const auto locations = known_protected_locations();
    // Refused by key alone: nothing is touched by asking.
    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    assert(deletion_refusal(key_of(resolved_of(temp)), locations) == nullptr);
    assert(deletion_refusal(key_of(L"C:\\"), locations) != nullptr);
    assert(deletion_refusal(key_of(L"\\\\server\\share"), locations) != nullptr);

    PWSTR profile{};
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Profile, 0, nullptr, &profile))) {
        const auto profile_key = key_of(resolved_of(profile));
        assert(deletion_refusal(profile_key, locations) != nullptr);
        assert(deletion_refusal(profile_key + L"\\neoastra-test-that-does-not-exist", locations) == nullptr);
        CoTaskMemFree(profile);
    }
}

// The file is refused and left untouched: a user-data root is a folder, and no deletion removes a file.
void test_a_file_is_refused_and_left_untouched(const std::wstring& base) {
    const auto file = base + L"\\not-a-folder.txt";
    write_file(file, "keep me");
    const auto outcome = delete_resolved_folder(resolved_of(file), 1s, never_canceled);
    assert(outcome.status == folder_deletion_status::refused);
    assert(fs::is_regular_file(fs::path(file)));
    assert(read_file(file) == "keep me");
}

// A similar name is not proof of ownership: deleting a missing root must leave its siblings intact.
void test_missing_root_preserves_siblings(const std::wstring& base) {
    const auto missing = base + L"\\victim";
    const auto leftover = base + L"\\victim.neoastra-deleting-1-1";
    fs::create_directories(leftover);
    write_file(leftover + L"\\left-behind.txt", "data of an earlier deletion");
    const auto outcome = delete_resolved_folder(resolved_of(missing), 1s, never_canceled);
    assert(outcome.status == folder_deletion_status::absent);
    assert(read_file(leftover + L"\\left-behind.txt") == "data of an earlier deletion");
    assert(delete_resolved_folder(resolved_of(missing), 1s, never_canceled).status == folder_deletion_status::absent);
}

// A folder that a process holds open stays whole and is reported in use after the wait; once it is let go, it is deleted.
void test_folder_in_use_is_left_whole(const std::wstring& base) {
    const auto busy = base + L"\\busy";
    fs::create_directories(busy + L"\\nested");
    write_file(busy + L"\\nested\\data.txt", "in use");
    const auto held = CreateFileW((busy + L"\\nested\\data.txt").c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
    assert(held != INVALID_HANDLE_VALUE);
    const auto refused = delete_resolved_folder(resolved_of(busy), 300ms, never_canceled);
    assert(refused.status == folder_deletion_status::in_use);
    assert(fs::is_regular_file(fs::path(busy + L"\\nested\\data.txt")));
    CloseHandle(held);

    const auto deleted = delete_resolved_folder(resolved_of(busy), 5s, never_canceled);
    assert(deleted.status == folder_deletion_status::deleted);
    assert(!fs::exists(fs::path(busy)));
}

// A deletion canceled before the folder is moved leaves it whole.
void test_canceled_deletion_leaves_the_folder(const std::wstring& base) {
    const auto folder = base + L"\\canceled";
    fs::create_directories(folder);
    write_file(folder + L"\\kept.txt", "kept");
    const auto outcome = delete_resolved_folder(resolved_of(folder), 1s, [] { return true; });
    assert(outcome.status == folder_deletion_status::canceled);
    assert(read_file(folder + L"\\kept.txt") == "kept");
}

// A folder of the test, with everything in it, is deleted, and nothing next to it is.
void test_folder_is_deleted_with_its_tree(const std::wstring& base) {
    const auto folder = base + L"\\tree";
    const auto sibling = base + L"\\tree-sibling";
    fs::create_directories(folder + L"\\a\\b");
    fs::create_directories(sibling);
    write_file(folder + L"\\a\\b\\data.txt", "tree");
    write_file(sibling + L"\\kept.txt", "sibling");
    const auto outcome = delete_resolved_folder(resolved_of(folder), 5s, never_canceled);
    assert(outcome.status == folder_deletion_status::deleted);
    assert(!fs::exists(fs::path(folder)));
    assert(read_file(sibling + L"\\kept.txt") == "sibling");
}

} // namespace

int main() {
    const auto base = make_base();
    test_relative_paths_follow_the_executable(base);
    test_spellings_of_one_folder_have_one_key(base);
    test_missing_parts_are_appended_to_the_existing_part(base);
    test_device_paths_are_not_resolved();
    test_protected_folders_are_refused();
    test_a_file_is_refused_and_left_untouched(base);
    test_missing_root_preserves_siblings(base);
    test_folder_in_use_is_left_whole(base);
    test_canceled_deletion_leaves_the_folder(base);
    test_folder_is_deleted_with_its_tree(base);
    // The base is the folder that this run created: removing it removes only what the tests made.
    std::error_code failure;
    fs::remove_all(fs::path(base), failure);
    assert(!failure);
    return 0;
}
