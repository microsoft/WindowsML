// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared command-line + interactive input collector for Windows ML Runtime samples.
//
// The goal is to keep each sample's main.cpp focused on the Runtime flow, not on
// argument plumbing. A sample constructs one SampleArgs and reads the few inputs it
// needs through typed getters:
//
//     SampleArgs args(argc, argv);
//     const DeviceArgs device = args.Device();                 // --device/--ep or a menu
//     const std::wstring prompt = args.Text(L"--prompt", 0, L"Prompt", kDefault);
//
// All of the parsing, the interactive prompting, and the --help text live here.
//
// Interactive mode: running a sample with NO arguments (for example, pressing F5 in
// Visual Studio) enters a guided mode where each getter prompts on the console with
// its label and default. Passing any argument switches to non-interactive parsing.
// Runtime-specific getters return DeviceArgs and provider names for the shared
// execution-target helpers.

#pragma once

#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <string>
#include <vector>

#include <windows.h>

#include "common.h"
#include "ep_catalog_utils.h"

namespace winml_args
{
// Reads one line from the console as a wide string, trimming the trailing newline.
// Returns false at end-of-input so callers fall back to defaults.
inline bool ReadConsoleLineWide(std::wstring& line)
{
    char buffer[4096] = {};
    if (!fgets(buffer, sizeof(buffer), stdin))
    {
        return false;
    }

    size_t length = strlen(buffer);
    while (length > 0 && (buffer[length - 1] == '\n' || buffer[length - 1] == '\r'))
    {
        buffer[--length] = '\0';
    }

    const int wide =
        MultiByteToWideChar(GetConsoleCP(), 0, buffer, static_cast<int>(length), nullptr, 0);
    line.assign(static_cast<size_t>(wide > 0 ? wide : 0), L'\0');
    if (wide > 0)
    {
        MultiByteToWideChar(GetConsoleCP(), 0, buffer, static_cast<int>(length), line.data(), wide);
    }

    return true;
}
} // namespace winml_args

// Collects a sample's inputs from the command line, or interactively when no
// arguments are supplied. Every getter documents one input; the machinery stays here.
class SampleArgs
{
public:
    SampleArgs(int argc, wchar_t** argv)
    {
        m_program = (argc > 0) ? argv[0] : L"sample";
        for (int i = 1; i < argc; ++i)
        {
            m_args.emplace_back(argv[i]);
        }

        m_interactive = m_args.empty();

        if (HasFlag(L"--help") || HasFlag(L"-h"))
        {
            PrintGenericUsage();
            exit(0);
        }
    }

    // True when the sample was launched with no arguments (e.g. F5 in Visual Studio).
    bool Interactive() const
    {
        return m_interactive;
    }

    bool Valid() const
    {
        return m_valid;
    }

    // Resolves the execution device. Non-interactive: --device / --ep. Interactive:
    // a short execution-target menu.
    DeviceArgs Device()
    {
        ResolveTarget();
        return m_device;
    }

    // The UTF-8 execution-provider name to pin, or an empty string for the
    // Runtime default.
    std::string Ep()
    {
        ResolveTarget();
        return m_ep;
    }

    // A boolean switch (e.g. --compile, --raw). Interactive: a y/N prompt with `label`
    // (pass nullptr to skip prompting and just read the flag).
    bool Flag(const wchar_t* name, const wchar_t* label = nullptr)
    {
        if (HasFlag(name))
        {
            return true;
        }

        if (m_interactive && label)
        {
            wprintf(L"%s [y/N]: ", label);
            fflush(stdout);
            std::wstring line;
            if (winml_args::ReadConsoleLineWide(line) && !line.empty() &&
                (line[0] == L'y' || line[0] == L'Y'))
            {
                return true;
            }
        }

        return false;
    }

    // A string value: --name VALUE, or (when positional >= 0) the leading bare token,
    // or (interactive) a prompt with `label` defaulting to `def`. Returns `def` if absent.
    std::wstring Text(const wchar_t* name, int positional, const wchar_t* label,
                      const std::wstring& def)
    {
        if (const wchar_t* flagValue = FindFlagValue(name))
        {
            return flagValue;
        }

        if (positional == 0 && !m_args.empty() && m_args[0][0] != L'-')
        {
            return m_args[0];
        }

        if (m_interactive)
        {
            wprintf(L"%s [%s]: ", label, def.c_str());
            fflush(stdout);
            std::wstring line;
            if (winml_args::ReadConsoleLineWide(line) && !line.empty())
            {
                return line;
            }
        }

        return def;
    }

    // An integer value: --name N, or (interactive) a prompt. Invalid values
    // mark the argument set invalid instead of silently becoming the default.
    int Int(const wchar_t* name, const wchar_t* label, int def)
    {
        for (size_t index = 0; index < m_args.size(); ++index)
        {
            if (m_args[index] != name)
            {
                continue;
            }

            if (index + 1 >= m_args.size())
            {
                wprintf(L"ERROR: %s requires an integer value.\n", name);
                m_valid = false;
                return def;
            }

            return ParseIntValue(name, m_args[index + 1], def);
        }

        if (m_interactive)
        {
            wprintf(L"%s [%d]: ", label, def);
            fflush(stdout);
            std::wstring line;
            if (winml_args::ReadConsoleLineWide(line) && !line.empty())
            {
                return ParseIntValue(name, line, def);
            }
        }

        return def;
    }

    // Resolves a model directory: --name VALUE wins; otherwise auto-discovery via the
    // shared model search using `probeRelPath` (a file expected under the directory,
    // e.g. "text_encoder\\model.onnx"), taking its parent. Interactive mode prompts for
    // a path only when auto-discovery fails. Returns empty when unresolved.
    std::wstring ModelDir(const wchar_t* name, const wchar_t* probeRelPath)
    {
        if (const wchar_t* flagValue = FindFlagValue(name))
        {
            return flagValue;
        }

        if (positional0_is_dir())
        {
            return m_args[0];
        }

        const std::wstring discovered = AutoDiscoverDir(probeRelPath);
        if (!discovered.empty())
        {
            return discovered;
        }

        if (m_interactive)
        {
            wprintf(L"Model directory (containing %s): ", probeRelPath);
            fflush(stdout);
            std::wstring line;
            if (winml_args::ReadConsoleLineWide(line) && !line.empty())
            {
                return line;
            }
        }

        return {};
    }

private:
    int ParseIntValue(const wchar_t* name, const std::wstring& text, int def)
    {
        errno = 0;
        wchar_t* end = nullptr;
        const long long value = wcstoll(text.c_str(), &end, 10);
        if (errno == ERANGE || end == text.c_str() || *end != L'\0' ||
            value < std::numeric_limits<int>::min() || value > std::numeric_limits<int>::max())
        {
            wprintf(L"ERROR: %s requires a valid integer value; got '%s'.\n", name, text.c_str());
            m_valid = false;
            return def;
        }

        return static_cast<int>(value);
    }

    // Auto-discovers a directory by locating `probeRelPath` via the shared model
    // search and returning the directory that contains the probe's first path segment.
    static std::wstring AutoDiscoverDir(const wchar_t* probeRelPath)
    {
        const std::wstring found = FindModelPath(probeRelPath);
        if (found.empty())
        {
            return {};
        }

        // Strip as many trailing segments as probeRelPath has, leaving the model dir.
        std::wstring probe = probeRelPath;
        size_t segments = 1;
        for (wchar_t c : probe)
        {
            if (c == L'\\' || c == L'/')
            {
                ++segments;
            }
        }

        std::wstring dir = found;
        for (size_t i = 0; i < segments; ++i)
        {
            const size_t sep = dir.find_last_of(L"\\/");
            if (sep == std::wstring::npos)
            {
                return {};
            }

            dir = dir.substr(0, sep);
        }

        return dir;
    }

    bool positional0_is_dir() const
    {
        if (m_args.empty() || m_args[0][0] == L'-')
        {
            return false;
        }

        const DWORD attrs = GetFileAttributesW(m_args[0].c_str());
        return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY);
    }

    bool HasFlag(const wchar_t* name) const
    {
        for (const std::wstring& a : m_args)
        {
            if (a == name)
            {
                return true;
            }
        }

        return false;
    }

    const wchar_t* FindFlagValue(const wchar_t* name) const
    {
        for (size_t i = 0; i + 1 < m_args.size(); ++i)
        {
            if (m_args[i] == name)
            {
                return m_args[i + 1].c_str();
            }
        }

        return nullptr;
    }

    // Resolves device, EP, and verbose once -- from flags in non-interactive mode,
    // or from a guided menu when interactive.
    void ResolveTarget()
    {
        if (m_resolved)
        {
            return;
        }

        m_resolved = true;

        if (!m_interactive)
        {
            m_device = ParseDeviceArgs(static_cast<int>(m_args.size()) + 1, BuildArgvShim());
            if (const wchar_t* ep = FindFlagValue(L"--ep"))
            {
                m_ep = WideToUtf8(ep);
                m_device.epName = m_ep;
            }

            if (HasFlag(L"--verbose"))
            {
                EnableVerboseSampleLogging();
            }

            return;
        }

        // Interactive execution-target menu. Provider options pin the entered catalog
        // provider; availability depends on installed packages and hardware.
        wprintf(L"\nSelect an execution target:\n");
        wprintf(L"  1) CPU                          (always available)\n");
        wprintf(L"  2) GPU                          (Runtime-owned provider)\n");
        wprintf(L"  3) GPU with an execution provider (enter a catalog name)\n");
        wprintf(L"  4) NPU with an execution provider (enter a catalog name)\n");

        wprintf(L"Choice [1]: ");
        fflush(stdout);

        std::wstring choice;
        winml_args::ReadConsoleLineWide(choice);

        if (choice == L"2")
        {
            m_device.deviceType = WINML_EXECUTION_TARGET_KIND_GPU;
        }
        else if (choice == L"3" || choice == L"4")
        {
            m_device.deviceType = (choice == L"4") ? WINML_EXECUTION_TARGET_KIND_NPU
                                                   : WINML_EXECUTION_TARGET_KIND_GPU;
            wprintf(
                L"Execution-provider catalog name (availability depends on installed packages): ");
            fflush(stdout);
            std::wstring ep;
            if (winml_args::ReadConsoleLineWide(ep) && !ep.empty())
            {
                m_ep = WideToUtf8(ep);
                m_device.epName = m_ep;
            }
        }
        else
        {
            m_device.deviceType = WINML_EXECUTION_TARGET_KIND_CPU;
        }

        wprintf(L"\n");
    }

    // ParseDeviceArgs expects a (argc, argv) pair; build a transient argv over m_args.
    wchar_t** BuildArgvShim()
    {
        m_shim.clear();
        m_shim.push_back(const_cast<wchar_t*>(m_program));
        for (std::wstring& a : m_args)
        {
            m_shim.push_back(a.data());
        }

        return m_shim.data();
    }

    void PrintGenericUsage() const
    {
        wprintf(L"Usage: %s [options]\n\n", m_program);
        wprintf(L"Run with no arguments for a guided, interactive mode.\n\n");
        wprintf(L"Common options:\n");
        wprintf(L"  --device cpu|gpu|npu   Execution device (default: cpu)\n");
        wprintf(L"  --ep <name>            Pin one installed execution provider\n");
        wprintf(
            L"                          Use --device gpu without --ep for Runtime-owned WebGPU\n");
        wprintf(
            L"  --verbose              Verbose Runtime logging (execution-provider node placement)\n");
        wprintf(L"  --help, -h             Show this message\n\n");
        wprintf(L"See the sample's README.md for the full option list.\n");
    }

    const wchar_t* m_program = L"sample";
    std::vector<std::wstring> m_args;
    std::vector<wchar_t*> m_shim;
    bool m_interactive = false;
    bool m_resolved = false;
    bool m_valid = true;
    DeviceArgs m_device;
    std::string m_ep;
};
