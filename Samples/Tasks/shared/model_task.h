// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Error reporting shared by the model-backed Task samples.

#pragma once

#include "task_runtime.h"

#include <cstdio>

namespace winmlsamples::tasks
{

inline bool IsNotSupported(HRESULT result) noexcept
{
    return result == HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
}

// Prints the HRESULT of a failed sample and what usually causes it.
inline void ReportFailure(LPCWSTR operation, HRESULT result) noexcept
{
    std::fwprintf(stderr, L"%ls failed: 0x%08X\n", operation, static_cast<unsigned int>(result));

    if (result == E_NOINTERFACE)
    {
        std::fwprintf(stderr, L"The Windows ML package deployed with this app does not provide "
                              L"this API. Build with a package that includes model-backed "
                              L"Tasks; see the model-backed README.\n");
    }
    else if (IsNotSupported(result))
    {
        std::fwprintf(stderr, L"Windows ML does not support this model, option or execution "
                              L"target for this Task.\n");
    }
    else if (result == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) ||
             result == HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND))
    {
        std::fwprintf(stderr, L"A model or input file was not found. Check the paths.\n");
    }
}

} // namespace winmlsamples::tasks
