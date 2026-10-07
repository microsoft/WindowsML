// Copyright (C) Microsoft Corporation. All rights reserved.
//
// numeric_utils.h -- IEEE 754 float16 <-> float32 conversion utilities.
//
// Used by samples that read or write float16 tensors (e.g., attention masks,
// logits). These are pure arithmetic conversions with no platform dependency.

#pragma once

#include <cstdint>
#include <cstring>

inline uint16_t FloatToFloat16(float value)
{
    uint32_t bits;
    memcpy(&bits, &value, sizeof(bits));

    uint16_t sign = static_cast<uint16_t>((bits >> 16) & 0x8000u);
    int32_t exponent = static_cast<int32_t>((bits >> 23) & 0xFF) - 127;
    uint32_t mantissa = bits & 0x7FFFFFu;

    if (exponent == 128)
    {
        return static_cast<uint16_t>(sign | (mantissa ? 0x7E00u : 0x7C00u));
    }

    if (exponent < -14)
    {
        if (exponent < -24)
        {
            return static_cast<uint16_t>(sign);
        }

        mantissa |= 0x800000u;
        uint32_t shift = static_cast<uint32_t>(-1 - exponent);
        mantissa >>= shift;
        return static_cast<uint16_t>(sign | (mantissa >> 13));
    }

    if (exponent > 15)
    {
        return static_cast<uint16_t>(sign | 0x7C00u);
    }

    mantissa += 0x00001000u;
    if (mantissa & 0x00800000u)
    {
        mantissa = 0;
        ++exponent;
    }

    if (exponent > 15)
    {
        return static_cast<uint16_t>(sign | 0x7C00u);
    }

    return static_cast<uint16_t>(sign | (static_cast<uint32_t>(exponent + 15) << 10) |
                                 (mantissa >> 13));
}

inline float Float16ToFloat(uint16_t value)
{
    uint32_t sign = (static_cast<uint32_t>(value) & 0x8000u) << 16;
    uint32_t exponent = (value >> 10) & 0x1Fu;
    uint32_t mantissa = value & 0x3FFu;

    uint32_t result;
    if (exponent == 0)
    {
        if (mantissa == 0)
        {
            result = sign;
        }
        else
        {
            exponent = 1;
            while (!(mantissa & 0x400u))
            {
                mantissa <<= 1;
                ++exponent;
            }

            mantissa &= 0x3FFu;
            result = sign | ((127 - 15 + 1 - exponent) << 23) | (mantissa << 13);
        }
    }
    else if (exponent == 31)
    {
        result = sign | 0x7F800000u | (mantissa << 13);
    }
    else
    {
        result = sign | ((exponent + 127 - 15) << 23) | (mantissa << 13);
    }

    float f;
    memcpy(&f, &result, sizeof(f));
    return f;
}
