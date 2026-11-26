#include "user_data_root_paths.hpp"

#include <knownfolders.h>
#include <shlobj.h>
#include <shlwapi.h>

#include <filesystem>
#include <system_error>
#include <thread>
#include <utility>
#include <vector>

using namespace std::chrono_literals;

namespace neo_windows_roots {
namespace {

// The name that a folder gets while it is moved aside: `<name>.neoastra-deleting-<process>-<tick>`.
constexpr std::wstring_view leftover_marker = L".neoastra-deleting-";

// The long-path form of a resolved path, which the Win32 file functions take without the 260-character limit.
std::wstring extended_path(const std::wstring& resolved) {
    if (resolved.starts_with(L"\\\\?\\")) return resolved;
    if (resolved.starts_with(L"\\\\")) return L"\\\\?\\UNC\\" + resolved.substr(2);
    return L"\\\\?\\" + resolved;
}

// WebView2 resolves relative user-data folders beside the executable, not under the process working directory.
bool full_path(const std::wstring& path, std::wstring& full, DWORD& error) {
    std::filesystem::path input(path);
    if (!input.is_absolute()) {
        if (input.has_root_path()) { error = ERROR_INVALID_NAME; return false; }
        std::wstring executable(MAX_PATH, L'\0');
        for (;;) {
            const auto length = GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()));
            if (!length) { error = GetLastError(); return false; }
            if (length < executable.size()) { executable.resize(length); break; }
            executable.resize(executable.size() * 2);
        }
        input = std::filesystem::path(executable).parent_path() / input;
    }
    std::wstring buffer(MAX_PATH, L'\0');
    for (;;) {
        const auto length = GetFullPathNameW(input.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
        if (length == 0) {
            error = GetLastError();
            return false;
        }
        if (length < buffer.size()) {
            buffer.resize(length);
            full = std::move(buffer);
            return true;
        }
        buffer.resize(length);
    }
}

// The final path of an open handle as a drive or network share path, without the `\\?\` prefix.
bool final_path_of(HANDLE handle, std::wstring& path, DWORD& error) {
    std::wstring buffer(MAX_PATH, L'\0');
    for (;;) {
        const auto length = GetFinalPathNameByHandleW(handle, buffer.data(), static_cast<DWORD>(buffer.size()),
                                                      FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (length == 0) {
            error = GetLastError();
            return false;
        }
        if (length < buffer.size()) {
            buffer.resize(length);
            break;
        }
        buffer.resize(length);
    }
    if (buffer.starts_with(L"\\\\?\\UNC\\")) path = L"\\\\" + buffer.substr(8);
    else if (buffer.starts_with(L"\\\\?\\")) path = buffer.substr(4);
    else path = std::move(buffer);
    return true;
}

// Splits `base` into the folder that holds its last name and that name. A root has no such parent.
bool split_last(const std::wstring& base, std::wstring& parent, std::wstring& leaf) {
    const auto separator = base.find_last_of(L'\\');
    if (separator == std::wstring::npos || PathIsRootW(base.c_str())) return false;
    leaf = base.substr(separator + 1);
    parent = base.substr(0, separator);
    if (parent.size() == 2 && parent[1] == L':') parent += L'\\';
    return !leaf.empty() && !parent.empty();
}

// Removes the trailing separators of a path that is not a root.
void trim_separators(std::wstring& path) {
    while (path.size() > 1 && path.back() == L'\\' && !PathIsRootW(path.c_str())) path.pop_back();
}

// Removes a tree that a deletion moved aside, retrying a few times while another process lets go of a file.
std::error_code remove_tree(const std::wstring& resolved) {
    std::error_code failure;
    for (int attempt = 0; attempt < 4; ++attempt) {
        failure.clear();
        static_cast<void>(std::filesystem::remove_all(std::filesystem::path(extended_path(resolved)), failure));
        if (!failure) return failure;
        std::this_thread::sleep_for(100ms);
    }
    return failure;
}

// Adds the resolved key of a known folder to `keys`, or nothing when the folder is unknown to the system.
void add_known_folder(std::vector<std::wstring>& keys, REFKNOWNFOLDERID id) {
    PWSTR raw{};
    if (FAILED(SHGetKnownFolderPath(id, 0, nullptr, &raw))) return;
    std::wstring resolved;
    DWORD error{};
    const bool ok = resolve_folder(raw, resolved, error);
    CoTaskMemFree(raw);
    if (ok) keys.push_back(key_of(resolved));
}

} // namespace

bool resolve_folder(const std::wstring& path, std::wstring& resolved, DWORD& error) {
    try {
        std::wstring full;
        if (!full_path(path, full, error)) return false;
        if (full.starts_with(L"\\\\.\\")) {
            error = ERROR_INVALID_NAME;
            return false;
        }
        trim_separators(full);

        // The existing part is opened and read as the file system names it; the part that does not exist yet is appended.
        std::vector<std::wstring> missing;
        std::wstring base = full;
        std::wstring final_path;
        for (;;) {
            const HANDLE handle = CreateFileW(extended_path(base).c_str(), FILE_READ_ATTRIBUTES,
                                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                              FILE_FLAG_BACKUP_SEMANTICS, nullptr);
            if (handle != INVALID_HANDLE_VALUE) {
                const bool ok = final_path_of(handle, final_path, error);
                CloseHandle(handle);
                if (!ok) return false;
                break;
            }
            const auto failure = GetLastError();
            std::wstring parent;
            std::wstring leaf;
            if ((failure != ERROR_FILE_NOT_FOUND && failure != ERROR_PATH_NOT_FOUND) || !split_last(base, parent, leaf)) {
                error = failure;
                return false;
            }
            missing.push_back(std::move(leaf));
            base = std::move(parent);
        }
        for (auto leaf = missing.rbegin(); leaf != missing.rend(); ++leaf) {
            if (final_path.back() != L'\\') final_path += L'\\';
            final_path += *leaf;
        }
        resolved = std::move(final_path);
        return true;
    } catch (...) {
        error = ERROR_NOT_ENOUGH_MEMORY;
        return false;
    }
}

std::wstring key_of(const std::wstring& resolved) {
    if (resolved.empty()) return {};
    const auto size = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, resolved.c_str(), static_cast<int>(resolved.size()),
                                    nullptr, 0, nullptr, nullptr, 0);
    if (size <= 0) return resolved;
    std::wstring key(static_cast<std::size_t>(size), L'\0');
    LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, resolved.c_str(), static_cast<int>(resolved.size()), key.data(), size,
                  nullptr, nullptr, 0);
    return key;
}

protected_locations known_protected_locations() {
    protected_locations locations;
    add_known_folder(locations.user_folders, FOLDERID_Profile);
    add_known_folder(locations.user_folders, FOLDERID_RoamingAppData);
    add_known_folder(locations.user_folders, FOLDERID_LocalAppData);
    add_known_folder(locations.user_folders, FOLDERID_LocalAppDataLow);
    add_known_folder(locations.user_folders, FOLDERID_ProgramData);
    add_known_folder(locations.system_folders, FOLDERID_Windows);
    add_known_folder(locations.system_folders, FOLDERID_System);
    add_known_folder(locations.system_folders, FOLDERID_ProgramFiles);
    add_known_folder(locations.system_folders, FOLDERID_ProgramFilesX86);
    add_known_folder(locations.system_folders, FOLDERID_ProgramFilesX64);
    return locations;
}

path_kind kind_of(const std::wstring& resolved, DWORD& error) {
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (GetFileAttributesExW(extended_path(resolved).c_str(), GetFileExInfoStandard, &attributes)) {
        return (attributes.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ? path_kind::folder : path_kind::file;
    }
    error = GetLastError();
    if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) return path_kind::missing;
    return path_kind::unknown;
}

folder_deletion_result delete_resolved_folder(const std::wstring& resolved, std::chrono::milliseconds wait,
                                              const std::function<bool()>& is_canceled) noexcept {
    folder_deletion_result result;
    try {
        DWORD inspection{};
        const auto kind = kind_of(resolved, inspection);
        if (kind == path_kind::file) {
            // A file is never moved nor removed: a user-data root is a folder.
            result.status = folder_deletion_status::refused;
            result.message = "The user-data root is a file, which is not deleted";
            return result;
        }
        if (kind == path_kind::unknown) {
            result.error = inspection;
            result.message = "The user-data root could not be inspected";
            return result;
        }

        std::wstring parent;
        std::wstring leaf;
        if (!split_last(resolved, parent, leaf)) {
            result.status = folder_deletion_status::refused;
            result.message = "The root of a drive is not a user-data root";
            return result;
        }
        if (kind == path_kind::missing) {
            result.status = folder_deletion_status::absent;
            return result;
        }

        const auto deadline = std::chrono::steady_clock::now() + wait;
        const auto aside = parent + L'\\' + leaf + std::wstring(leftover_marker) + std::to_wstring(GetCurrentProcessId()) + L'-' +
                           std::to_wstring(GetTickCount64());
        for (;;) {
            if (is_canceled()) {
                result.status = folder_deletion_status::canceled;
                result.message = "The deletion of the user-data root was canceled before the folder was moved";
                return result;
            }
            if (MoveFileExW(extended_path(resolved).c_str(), extended_path(aside).c_str(), 0)) break;
            const auto failure = GetLastError();
            if (failure == ERROR_FILE_NOT_FOUND || failure == ERROR_PATH_NOT_FOUND) {
                result.status = folder_deletion_status::absent;
                return result;
            }
            const bool busy = failure == ERROR_ACCESS_DENIED || failure == ERROR_SHARING_VIOLATION || failure == ERROR_LOCK_VIOLATION;
            if (!busy) {
                result.status = folder_deletion_status::failed;
                result.error = failure;
                result.message = "The user-data root could not be moved aside for deletion";
                return result;
            }
            if (std::chrono::steady_clock::now() >= deadline) {
                result.status = folder_deletion_status::in_use;
                result.error = failure;
                result.message = "A process still uses the user-data root, or it cannot be deleted";
                return result;
            }
            std::this_thread::sleep_for(100ms);
        }

        if (const auto failure = remove_tree(aside)) {
            result.status = folder_deletion_status::failed;
            result.error = static_cast<DWORD>(failure.value());
            result.message = "The user-data root was moved aside but not deleted";
            return result;
        }
        result.status = folder_deletion_status::deleted;
        return result;
    } catch (...) {
        result.status = folder_deletion_status::failed;
        result.message = "The user-data root could not be deleted";
        return result;
    }
}

} // namespace neo_windows_roots
