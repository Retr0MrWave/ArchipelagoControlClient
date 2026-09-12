#pragma once

#include <cstdint>
#include <cstdio>
#include <string>

namespace ap::json {

inline std::string quote(const std::string& text) {
    std::string out = "\"";
    for (char c : text) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    char escaped[8];
                    snprintf(escaped, sizeof escaped, "\\u%04x", static_cast<unsigned char>(c));
                    out += escaped;
                } else {
                    out += c;
                }
        }
    }
    return out + "\"";
}

/// A deliberately minimal JSON writer: the shim only ever emits flat objects of numbers, strings,
/// booleans and pre-rendered arrays. Writing this is smaller than taking a dependency, and the
/// client reads it with System.Text.Json, which is where any real parsing belongs.
class Object {
public:
    Object& num(const char* key, uint64_t value) { return field(key, std::to_string(value)); }
    Object& inum(const char* key, int64_t value) { return field(key, std::to_string(value)); }
    Object& flag(const char* key, bool value) { return field(key, value ? "true" : "false"); }
    Object& text(const char* key, const std::string& value) { return field(key, quote(value)); }

    /// Insert an already-rendered fragment, for arrays and nested objects.
    Object& raw(const char* key, const std::string& rendered) { return field(key, rendered); }

    std::string str() const { return "{" + body_ + "}"; }

private:
    Object& field(const char* key, const std::string& rendered) {
        if (!body_.empty()) body_ += ',';
        body_ += quote(key);
        body_ += ':';
        body_ += rendered;
        return *this;
    }

    std::string body_;
};

}  // namespace ap::json
