#pragma once

#include <algorithm>
#include <filesystem>
#include <optional>
#include <system_error>

// The path rules of the user-data roots of the WebKitGTK backend. They use only the file system, not GLib or WebKitGTK, so
// that they can be tested alone.

// The key of a root: an absolute path, resolved for the part that exists, without a trailing separator. Sessions and deletions
// use it, so that one directory has one key however it is spelled. Fails closed: when a part cannot be resolved there is no key,
// and the caller refuses the root rather than match it by another spelling.
inline std::optional<std::filesystem::path> resolved_user_data_root(const std::filesystem::path& root, std::error_code& failure) {
    failure.clear();
    const auto absolute = std::filesystem::absolute(root, failure);
    if (failure) return std::nullopt;
    auto path = std::filesystem::weakly_canonical(absolute, failure);
    if (failure) return std::nullopt;
    while (path.has_relative_path() && !path.has_filename()) path = path.parent_path();
    return path;
}

// Whether `ancestor` is `path` or one of its parents. Components are compared whole: "/a" is no parent of "/ab".
inline bool is_ancestor_or_self(const std::filesystem::path& ancestor, const std::filesystem::path& path) {
    return std::mismatch(ancestor.begin(), ancestor.end(), path.begin(), path.end()).first == ancestor.end();
}

// Whether one of two roots is the other, a parent of it, or inside it: deleting one of them reaches what the other one stores.
inline bool overlaps(const std::filesystem::path& first, const std::filesystem::path& second) {
    return is_ancestor_or_self(first, second) || is_ancestor_or_self(second, first);
}
