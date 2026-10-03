// Copyright (c) 2018 Samsung Electronics Co., LTD
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "utils/utf.h"

#include <codecvt>
#include <locale>

namespace netcoredbg
{

static std::wstring_convert<std::codecvt_utf8_utf16<WCHAR>,WCHAR> convert;

std::string to_utf8(const WCHAR *wstr)
{
    return convert.to_bytes(wstr);
}

std::string to_utf8(WCHAR wch)
{
    return convert.to_bytes(wch);
}

std::string to_utf8(const WCHAR *wstr, size_t length)
{
    std::string out;
    out.reserve(length);
    for (size_t i = 0; i < length; i++)
    {
        uint32_t cp = wstr[i];
        if (IsUtf16HighSurrogate(cp) && i + 1 < length && IsUtf16LowSurrogate(wstr[i + 1]))
        {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (uint32_t(wstr[i + 1]) - 0xDC00);
            i++;
        }
        else if (IsUtf16HighSurrogate(cp) || IsUtf16LowSurrogate(cp))
        {
            cp = 0xFFFD;
        }

        if (cp < 0x80)
        {
            out += char(cp);
        }
        else if (cp < 0x800)
        {
            out += char(0xC0 | (cp >> 6));
            out += char(0x80 | (cp & 0x3F));
        }
        else if (cp < 0x10000)
        {
            out += char(0xE0 | (cp >> 12));
            out += char(0x80 | ((cp >> 6) & 0x3F));
            out += char(0x80 | (cp & 0x3F));
        }
        else
        {
            out += char(0xF0 | (cp >> 18));
            out += char(0x80 | ((cp >> 12) & 0x3F));
            out += char(0x80 | ((cp >> 6) & 0x3F));
            out += char(0x80 | (cp & 0x3F));
        }
    }
    return out;
}

WSTRING to_utf16(const std::string &utf8)
{
    return convert.from_bytes(utf8);
}

} // namespace netcoredbg
