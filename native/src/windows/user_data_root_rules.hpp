#pragma once

// The rules of the user-data roots of the Windows backend that do not call Windows: how two folder keys relate, which folders a
// deletion refuses, and the registry that keeps the environments and the deletions of one process apart. A key is the canonical
// path of a folder in lowercase (see user_data_root_paths.hpp), so that two spellings of one folder are one key.

#include <cstddef>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <string_view>
#include <vector>

namespace neo_windows_roots {

// Whether `path` is `ancestor` or lies under it. A folder name ends at a separator, and a drive root keeps its separator.
inline bool is_same_or_inside(std::wstring_view path, std::wstring_view ancestor) noexcept {
    if (ancestor.empty() || path.size() < ancestor.size() || path.compare(0, ancestor.size(), ancestor) != 0) return false;
    return path.size() == ancestor.size() || ancestor.back() == L'\\' || path[ancestor.size()] == L'\\';
}

// Whether one of two folders is the other or lies under it.
inline bool overlaps(std::wstring_view first, std::wstring_view second) noexcept {
    return is_same_or_inside(first, second) || is_same_or_inside(second, first);
}

// A drive root key, such as `c:\`.
inline bool is_drive_root(std::wstring_view key) noexcept {
    return key.size() == 3 && key[1] == L':' && key[2] == L'\\' &&
           ((key[0] >= L'a' && key[0] <= L'z') || (key[0] >= L'A' && key[0] <= L'Z'));
}

// A network share root key, such as `\\server\share`: two leading separators, and no separator after the share.
inline bool is_share_root(std::wstring_view key) noexcept {
    while (!key.empty() && key.back() == L'\\') key.remove_suffix(1);
    if (key.size() < 5 || key.compare(0, 2, L"\\\\") != 0) return false;
    std::size_t separators = 0;
    for (const auto character : key) separators += character == L'\\' ? 1 : 0;
    return separators == 3;
}

// Whether a key is a path of a drive, such as `c:\a`, or of a network share, and not a device path such as `\\?\Volume{...}` or `\\.\`.
inline bool is_dos_key(std::wstring_view key) noexcept {
    const bool drive = key.size() >= 3 && key[1] == L':' && key[2] == L'\\' &&
                       ((key[0] >= L'a' && key[0] <= L'z') || (key[0] >= L'A' && key[0] <= L'Z'));
    const bool share = key.size() > 2 && key.compare(0, 2, L"\\\\") == 0 && key[2] != L'.' && key[2] != L'?';
    return drive || share;
}

// The folders that a deletion must not touch. A user-data root must not be one of the user folders, and must not contain one:
// deleting it would delete the folders of the user. It must not be a system folder nor be inside one, which holds Windows and
// the installed programs.
struct protected_locations {
    std::vector<std::wstring> user_folders;
    std::vector<std::wstring> system_folders;
};

// The reason why the folder of `key` cannot be deleted as a user-data root, or nullptr when it can.
inline const char* deletion_refusal(std::wstring_view key, const protected_locations& locations) noexcept {
    if (!is_dos_key(key)) return "The user-data root is not a drive or network share path";
    if (is_drive_root(key)) return "The root of a drive is not a user-data root";
    if (is_share_root(key)) return "The root of a network share is not a user-data root";
    for (const auto& folder : locations.user_folders) {
        if (is_same_or_inside(folder, key)) return "The user-data root contains a Windows user folder";
    }
    for (const auto& folder : locations.system_folders) {
        if (is_same_or_inside(key, folder)) return "The user-data root is inside a Windows or program folder";
    }
    return nullptr;
}

// Keeps the environments that live on folders apart from the deletions of folders, for the environments and deletions of one
// process that several UI threads start. A deletion of a folder waits while an environment on an overlapping folder lives, and an
// environment is refused while a deletion of an overlapping folder is pending. Every operation takes the one lock.
class user_data_root_registry final {
public:
    enum class deletion_start { started, environment_alive, deleting };

    // Registers an environment on `key`. Refused, and nothing is registered, while a deletion of an overlapping folder is pending.
    bool try_acquire_environment(const std::wstring& key) {
        std::lock_guard lock(_mutex);
        for (const auto& pending : _deleting) {
            if (overlaps(key, pending)) return false;
        }
        ++_environments[key];
        return true;
    }

    // Unregisters an environment that try_acquire_environment registered on `key`.
    void release_environment(const std::wstring& key) noexcept {
        std::lock_guard lock(_mutex);
        const auto found = _environments.find(key);
        if (found != _environments.end() && --found->second == 0) _environments.erase(found);
    }

    // Registers a deletion of `key` when no environment on an overlapping folder lives and no other deletion of one is pending.
    deletion_start begin_deletion(const std::wstring& key) {
        std::lock_guard lock(_mutex);
        for (const auto& [environment, count] : _environments) {
            if (overlaps(key, environment)) return deletion_start::environment_alive;
        }
        for (const auto& pending : _deleting) {
            if (overlaps(key, pending)) return deletion_start::deleting;
        }
        _deleting.insert(key);
        return deletion_start::started;
    }

    // Unregisters a deletion that begin_deletion registered on `key`.
    void end_deletion(const std::wstring& key) noexcept {
        std::lock_guard lock(_mutex);
        _deleting.erase(key);
    }

    // The number of distinct folders with an environment, and of pending deletions, for the tests.
    std::size_t environment_folders() const {
        std::lock_guard lock(_mutex);
        return _environments.size();
    }
    std::size_t pending_deletions() const {
        std::lock_guard lock(_mutex);
        return _deleting.size();
    }

private:
    mutable std::mutex _mutex;
    std::map<std::wstring, std::size_t> _environments;
    std::set<std::wstring> _deleting;
};

} // namespace neo_windows_roots
