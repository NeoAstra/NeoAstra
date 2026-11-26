#include "../src/windows/user_data_root_rules.hpp"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <atomic>
#include <cassert>
#include <string>
#include <thread>
#include <vector>

using namespace neo_windows_roots;
using registry_t = user_data_root_registry;

namespace {

void test_containment() {
    assert(is_same_or_inside(L"c:\\a\\b", L"c:\\a"));
    assert(is_same_or_inside(L"c:\\a", L"c:\\a"));
    assert(!is_same_or_inside(L"c:\\ab", L"c:\\a"));  // A name is not a prefix of another name.
    assert(!is_same_or_inside(L"c:\\a", L"c:\\a\\b"));
    assert(is_same_or_inside(L"c:\\x", L"c:\\"));      // A drive root contains every folder of the drive.
    assert(!is_same_or_inside(L"d:\\x", L"c:\\"));
    assert(!is_same_or_inside(L"c:\\a", L""));          // An empty key is nothing, not everything.
    assert(overlaps(L"c:\\a", L"c:\\a\\b") && overlaps(L"c:\\a\\b", L"c:\\a"));
    assert(!overlaps(L"c:\\a", L"c:\\b"));
}

void test_kinds() {
    assert(is_drive_root(L"c:\\") && is_drive_root(L"Z:\\"));
    assert(!is_drive_root(L"c:") && !is_drive_root(L"c:\\a") && !is_drive_root(L"1:\\"));
    assert(is_share_root(L"\\\\server\\share"));
    assert(!is_share_root(L"\\\\server\\share\\a") && !is_share_root(L"\\\\server"));
    assert(is_dos_key(L"c:\\a") && is_dos_key(L"\\\\server\\share\\a"));
    assert(!is_dos_key(L"\\\\?\\volume{1}\\") && !is_dos_key(L"\\\\.\\c:\\a") && !is_dos_key(L"relative"));
}

void test_refusals() {
    protected_locations locations;
    locations.user_folders = {L"c:\\users\\alex", L"c:\\users\\alex\\appdata\\local"};
    locations.system_folders = {L"c:\\windows", L"c:\\program files"};

    assert(deletion_refusal(L"c:\\users\\alex\\appdata\\local\\neoastra", locations) == nullptr);
    assert(deletion_refusal(L"c:\\users\\alex\\appdata\\local\\neoastra\\web", locations) == nullptr);
    assert(deletion_refusal(L"c:\\users\\alex", locations) != nullptr);      // The profile itself.
    assert(deletion_refusal(L"c:\\users", locations) != nullptr);           // A folder that contains the profile.
    assert(deletion_refusal(L"c:\\users\\alex\\appdata\\local", locations) != nullptr);
    assert(deletion_refusal(L"c:\\windows\\system32\\neoastra", locations) != nullptr);
    assert(deletion_refusal(L"c:\\program files", locations) != nullptr);
    assert(deletion_refusal(L"c:\\", locations) != nullptr);
    assert(deletion_refusal(L"\\\\server\\share", locations) != nullptr);
    assert(deletion_refusal(L"\\\\?\\volume{1}\\a", locations) != nullptr);
    assert(deletion_refusal(L"d:\\data\\neoastra", locations) == nullptr);
}

void test_registry_nesting() {
    registry_t registry;
    assert(registry.try_acquire_environment(L"c:\\data\\app"));
    // A deletion of a folder that contains or lies in a live environment is refused, and nothing becomes pending.
    assert(registry.begin_deletion(L"c:\\data") == registry_t::deletion_start::environment_alive);
    assert(registry.begin_deletion(L"c:\\data\\app\\web") == registry_t::deletion_start::environment_alive);
    assert(registry.pending_deletions() == 0);
    // A sibling with the same prefix is another folder.
    assert(registry.begin_deletion(L"c:\\data\\app2") == registry_t::deletion_start::started);
    assert(registry.pending_deletions() == 1);
    registry.end_deletion(L"c:\\data\\app2");
    assert(registry.pending_deletions() == 0);

    // While a deletion is pending, an environment on an overlapping folder is refused, and a second deletion too.
    assert(registry.begin_deletion(L"c:\\other") == registry_t::deletion_start::started);
    assert(!registry.try_acquire_environment(L"c:\\other\\child"));
    assert(!registry.try_acquire_environment(L"c:"));
    assert(registry.begin_deletion(L"c:\\other\\child") == registry_t::deletion_start::deleting);
    assert(registry.try_acquire_environment(L"c:\\unrelated"));
    registry.end_deletion(L"c:\\other");
    assert(registry.try_acquire_environment(L"c:\\other\\child"));

    registry.release_environment(L"c:\\data\\app");
    registry.release_environment(L"c:\\unrelated");
    registry.release_environment(L"c:\\other\\child");
    registry.release_environment(L"c:\\other\\child");   // A second release of an absent key changes nothing.
    assert(registry.environment_folders() == 0);
    assert(registry.begin_deletion(L"c:\\data") == registry_t::deletion_start::started);
    registry.end_deletion(L"c:\\data");
}

void test_registry_counts_environments_on_one_folder() {
    registry_t registry;
    assert(registry.try_acquire_environment(L"c:\\data"));
    assert(registry.try_acquire_environment(L"c:\\data"));
    registry.release_environment(L"c:\\data");
    assert(registry.begin_deletion(L"c:\\data") == registry_t::deletion_start::environment_alive);
    registry.release_environment(L"c:\\data");
    assert(registry.begin_deletion(L"c:\\data") == registry_t::deletion_start::started);
    registry.end_deletion(L"c:\\data");
}

// Several UI threads of several applications share one registry. The checks below hold while they run: an environment is never
// registered while a deletion of an overlapping folder is pending, and a deletion never starts while an environment on an
// overlapping folder is registered.
void test_registry_threads() {
    registry_t registry;
    const std::vector<std::wstring> folders{L"c:\\data", L"c:\\data\\app", L"c:\\data\\app\\web", L"c:\\other"};
    // Per folder: how many environments are between their acquire and release, and how many deletions are between their begin and end.
    std::vector<std::atomic<int>> active(folders.size());
    std::vector<std::atomic<int>> deleting(folders.size());
    std::atomic<int> violations{0};
    const auto overlapping = [&](std::size_t first, std::size_t second) { return overlaps(folders[first], folders[second]); };

    std::vector<std::thread> threads;
    for (std::size_t thread = 0; thread < 4; ++thread) {
        threads.emplace_back([&, thread] {
            for (std::size_t round = 0; round < 2000; ++round) {
                const auto index = (thread + round) % folders.size();
                if (registry.try_acquire_environment(folders[index])) {
                    // An environment is registered only while no deletion of an overlapping folder is pending.
                    for (std::size_t other = 0; other < folders.size(); ++other) {
                        if (overlapping(index, other) && deleting[other].load() != 0) violations.fetch_add(1);
                    }
                    active[index].fetch_add(1);
                    active[index].fetch_sub(1);
                    registry.release_environment(folders[index]);
                }
            }
        });
        threads.emplace_back([&, thread] {
            for (std::size_t round = 0; round < 2000; ++round) {
                const auto index = (thread + round) % folders.size();
                if (registry.begin_deletion(folders[index]) == registry_t::deletion_start::started) {
                    // A deletion starts only while no environment on an overlapping folder is registered.
                    deleting[index].fetch_add(1);
                    for (std::size_t other = 0; other < folders.size(); ++other) {
                        if (overlapping(index, other) && active[other].load() != 0) violations.fetch_add(1);
                    }
                    deleting[index].fetch_sub(1);
                    registry.end_deletion(folders[index]);
                }
            }
        });
    }
    for (auto& thread : threads) thread.join();
    assert(violations.load() == 0);
    assert(registry.environment_folders() == 0);
    assert(registry.pending_deletions() == 0);
}

} // namespace

int main() {
    test_containment();
    test_kinds();
    test_refusals();
    test_registry_nesting();
    test_registry_counts_environments_on_one_folder();
    test_registry_threads();
    return 0;
}
