#pragma once

// The paths of the user-data roots of the Windows backend: the one spelling of each folder that the registry compares, and the
// deletion of a folder, which moves it aside before it removes it.

#include "user_data_root_rules.hpp"

#include <windows.h>

#include <chrono>
#include <functional>
#include <string>

namespace neo_windows_roots {

// Resolves the path of a folder to the path that names it. Relative paths are taken from the directory of the executable, `.` and `..` are
// removed, and the existing part of the path is read as the file system names it (the final path), so that junctions, symbolic
// links, short names, and drive letters of one folder give one path. The part that does not exist yet is appended as written.
// Fails with a Win32 error in `error` for a path that cannot be read, such as a device path.
bool resolve_folder(const std::wstring& path, std::wstring& resolved, DWORD& error);

// The key of a resolved path: its lowercase form in the invariant culture, so that two keys of one folder are equal.
std::wstring key_of(const std::wstring& resolved);

// The protected folders of the user and of the system, resolved to keys. A folder that cannot be resolved is left out.
protected_locations known_protected_locations();

// Whether a resolved path names an existing file that is not a folder. A deletion never moves or removes such a file, so
// it refuses the path. `error` is set when the path cannot be inspected.
enum class path_kind { missing, folder, file, unknown };
path_kind kind_of(const std::wstring& resolved, DWORD& error);

enum class folder_deletion_status { deleted, absent, canceled, in_use, refused, failed };

struct folder_deletion_result {
    folder_deletion_status status{folder_deletion_status::failed};
    DWORD error{};
    const char* message{};
};

// Deletes the folder at `resolved` with everything in it. The folder is moved aside
// first, which waits, for at most `wait`, until no process uses it any more, and then removed. A file is refused and left
// untouched. `is_canceled` is asked before the folder is moved; once it is moved, the removal finishes. Never throws.
folder_deletion_result delete_resolved_folder(const std::wstring& resolved, std::chrono::milliseconds wait,
                                              const std::function<bool()>& is_canceled) noexcept;

} // namespace neo_windows_roots
