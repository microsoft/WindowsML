// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Win32 front-end for the Whisper sample: the window, buttons, worker threads,
// and message loop that drive interactive record/play/transcribe. This is
// standard desktop-app plumbing kept out of main.cpp so the sample's WinML
// Runtime usage stays front and center. The transcription itself is delegated to
// Transcribe() (defined in main.cpp) so the UI and console paths share one
// implementation.

#pragma once

#include <cmath>
#include <functional>
#include <string>
#include <unordered_map>
#include <vector>

#include <Windows.h>
#include <mmsystem.h>

#include <WinMLRuntime.h>

#include "common.h"
#include "audio_utils.h"
#include "whisper_tokenizer.h"

#pragma comment(lib, "winmm.lib")

// Defined in main.cpp. Runs the encoder->decoder transcription on already-
// prepared pipelines/stages, reporting status and decoded tokens through callbacks.
HRESULT Transcribe(IWinMLExecutionTarget* target, IWinMLPipeline* encoderPipeline,
                   IWinMLStage* encoderStage, IWinMLPipeline* decoderPipeline,
                   IWinMLStage* decoderStage, const std::unordered_map<int64_t, std::string>& vocab,
                   const float* samples, size_t sampleCount, uint32_t sampleRate,
                   const std::function<void(const wchar_t*)>& onStatus,
                   const std::function<void(const std::string&)>& onToken,
                   std::vector<int64_t>& generatedTokensOut, std::string& transcriptOut,
                   WhisperStopReason& stopReasonOut);

static constexpr float kRecordSeconds = 5.0f;

static constexpr int IDC_STATUS = 101;
static constexpr int IDC_BTN_RECORD = 102;
static constexpr int IDC_BTN_PLAY = 103;
static constexpr int IDC_BTN_TRANSCRIBE = 104;
static constexpr int IDC_OUTPUT = 105;

#define WM_RECORDING_DONE (WM_USER + 1)
#define WM_PLAYBACK_DONE (WM_USER + 2)
#define WM_TRANSCRIBE_DONE (WM_USER + 3)
#define WM_STATUS_UPDATE (WM_USER + 4)

static constexpr int kClientW = 580;
static constexpr int kClientH = 430;

enum class AppState
{
    Idle,
    Recording,
    Playing,
    Transcribing
};

// WinML objects are loaded once on the main thread and then borrowed by the
// worker threads, so the UI and console paths exercise the same object lifetimes.
struct AppContext
{
    HWND hwnd = nullptr;
    HWND hStatus = nullptr;
    HWND hBtnRecord = nullptr;
    HWND hBtnPlay = nullptr;
    HWND hBtnTranscribe = nullptr;
    HWND hOutput = nullptr;
    HFONT hFont = nullptr;

    AppState state = AppState::Idle;
    HANDLE hThread = nullptr;
    bool hasAudio = false;
    volatile bool stopRecording = false;

    std::vector<float> audioSamples;

    ComPtr<IWinMLRuntime> runtime;
    ComPtr<IWinMLExecutionTarget> target;
    ComPtr<IWinMLModel> encoderModel;
    ComPtr<IWinMLModel> decoderModel;
    ComPtr<IWinMLPipeline> encoderPipeline;
    ComPtr<IWinMLPipeline> decoderPipeline;
    ComPtr<IWinMLStage> encoderStage;
    ComPtr<IWinMLStage> decoderStage;
    std::unordered_map<int64_t, std::string> vocab;
};

static AppContext g_app;

static void SetStatus(const wchar_t* text)
{
    SetWindowTextW(g_app.hStatus, text);
}

static void UpdateButtons()
{
    switch (g_app.state)
    {
    case AppState::Idle:
        SetWindowTextW(g_app.hBtnRecord, L"Record (5s)");
        EnableWindow(g_app.hBtnRecord, TRUE);
        EnableWindow(g_app.hBtnPlay, g_app.hasAudio ? TRUE : FALSE);
        EnableWindow(g_app.hBtnTranscribe, g_app.hasAudio ? TRUE : FALSE);
        break;

    case AppState::Recording:
        SetWindowTextW(g_app.hBtnRecord, L"Stop");
        EnableWindow(g_app.hBtnRecord, TRUE);
        EnableWindow(g_app.hBtnPlay, FALSE);
        EnableWindow(g_app.hBtnTranscribe, FALSE);
        break;

    case AppState::Playing:
    case AppState::Transcribing:
        EnableWindow(g_app.hBtnRecord, FALSE);
        EnableWindow(g_app.hBtnPlay, FALSE);
        EnableWindow(g_app.hBtnTranscribe, FALSE);
        break;
    }
}

// Posts a status string from a worker thread to the UI thread, which owns and
// frees the copy. Worker threads must never touch the HWND directly.
static void PostStatusFromThread(const wchar_t* msg)
{
    wchar_t* copy = _wcsdup(msg);
    if (copy && !PostMessage(g_app.hwnd, WM_STATUS_UPDATE, reinterpret_cast<WPARAM>(copy), 0))
    {
        free(copy);
    }
}

static DWORD PostTranscriptionResult(const wchar_t* result, DWORD exitCode, LPARAM status)
{
    wchar_t* copy = _wcsdup(result);
    if (copy &&
        !PostMessage(g_app.hwnd, WM_TRANSCRIBE_DONE, reinterpret_cast<WPARAM>(copy), status))
    {
        free(copy);
    }

    return exitCode;
}

// WASAPI shared-mode capture -> float32 mono 16 kHz.
static DWORD WINAPI RecordThread(LPVOID /*param*/)
{
    WavData wav;
    HRESULT hr = CaptureFromMicrophone(kRecordSeconds, wav, &g_app.stopRecording);

    if (SUCCEEDED(hr) && !wav.samples.empty())
    {
        g_app.audioSamples = std::move(wav.samples);
        g_app.hasAudio = true;
        PostMessage(g_app.hwnd, WM_RECORDING_DONE, 1, 0);
    }
    else
    {
        wprintf(L"  Recording failed (0x%08X)\n", hr);
        PostMessage(g_app.hwnd, WM_RECORDING_DONE, 0, 0);
    }

    return 0;
}

// PCM playback of the captured audio via the waveOut API.
static DWORD WINAPI PlayThread(LPVOID /*param*/)
{
    size_t count = g_app.audioSamples.size();
    if (count == 0)
    {
        PostMessage(g_app.hwnd, WM_PLAYBACK_DONE, 0, 0);
        return 1;
    }

    std::vector<int16_t> pcm16(count);
    for (size_t i = 0; i < count; ++i)
    {
        float s = g_app.audioSamples[i];
        if (s > 1.0f)
        {
            s = 1.0f;
        }

        if (s < -1.0f)
        {
            s = -1.0f;
        }

        pcm16[i] = static_cast<int16_t>(s * 32767.0f);
    }

    WAVEFORMATEX wfx = {};
    wfx.wFormatTag = WAVE_FORMAT_PCM;
    wfx.nChannels = 1;
    wfx.nSamplesPerSec = kWhisperSampleRate;
    wfx.wBitsPerSample = 16;
    wfx.nBlockAlign = wfx.nChannels * wfx.wBitsPerSample / 8;
    wfx.nAvgBytesPerSec = wfx.nSamplesPerSec * wfx.nBlockAlign;

    HANDLE hEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    HWAVEOUT hWaveOut = nullptr;
    MMRESULT mr = waveOutOpen(&hWaveOut, WAVE_MAPPER, &wfx, reinterpret_cast<DWORD_PTR>(hEvent), 0,
                              CALLBACK_EVENT);
    if (mr != MMSYSERR_NOERROR)
    {
        wprintf(L"  Playback failed: waveOutOpen error %u\n", mr);
        CloseHandle(hEvent);
        PostMessage(g_app.hwnd, WM_PLAYBACK_DONE, 0, 0);
        return 1;
    }

    WAVEHDR header = {};
    header.lpData = reinterpret_cast<LPSTR>(pcm16.data());
    header.dwBufferLength = static_cast<DWORD>(count * sizeof(int16_t));
    waveOutPrepareHeader(hWaveOut, &header, sizeof(header));

    ResetEvent(hEvent);
    waveOutWrite(hWaveOut, &header, sizeof(header));

    while (!(header.dwFlags & WHDR_DONE))
    {
        WaitForSingleObject(hEvent, 100);
    }

    waveOutUnprepareHeader(hWaveOut, &header, sizeof(header));
    waveOutClose(hWaveOut);
    CloseHandle(hEvent);

    PostMessage(g_app.hwnd, WM_PLAYBACK_DONE, 1, 0);
    return 0;
}

// Runs the shared Transcribe() core on a worker thread: status updates flow to
// the UI, decoded tokens stream to the console, and the final transcript is
// posted to the output control.
static DWORD WINAPI TranscribeThread(LPVOID /*param*/)
{
    std::vector<int64_t> generatedTokens;
    std::string transcript;
    WhisperStopReason stopReason = WhisperStopReason::Capacity;
    HRESULT hr = Transcribe(
        g_app.target.Get(), g_app.encoderPipeline.Get(), g_app.encoderStage.Get(),
        g_app.decoderPipeline.Get(), g_app.decoderStage.Get(), g_app.vocab,
        g_app.audioSamples.data(), g_app.audioSamples.size(), kWhisperSampleRate,
        [](const wchar_t* status) {
            PostStatusFromThread(status);
        },
        [](const std::string& token) {
            wprintf(L"%s", Utf8ToWide(token).c_str());
            fflush(stdout);
        },
        generatedTokens, transcript, stopReason);

    if (FAILED(hr))
    {
        return PostTranscriptionResult(L"[Transcription failed]", 1, -1);
    }

    return PostTranscriptionResult(Utf8ToWide(transcript).c_str(), 0,
                                   static_cast<LPARAM>(stopReason));
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg)
    {
    case WM_CREATE:
    {
        HINSTANCE hInst = reinterpret_cast<HINSTANCE>(GetWindowLongPtrW(hwnd, GWLP_HINSTANCE));

        g_app.hFont = CreateFontW(-14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                  OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                  DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");

        g_app.hStatus = CreateWindowExW(0, L"STATIC", L"Ready. Click [Record] to capture audio.",
                                        WS_CHILD | WS_VISIBLE | SS_LEFT, 20, 15, 540, 22, hwnd,
                                        reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDC_STATUS)),
                                        hInst, nullptr);

        g_app.hBtnRecord = CreateWindowExW(
            0, L"BUTTON", L"Record (5s)", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 30, 50, 160, 35,
            hwnd, reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDC_BTN_RECORD)), hInst, nullptr);

        g_app.hBtnPlay = CreateWindowExW(
            0, L"BUTTON", L"Play", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON | WS_DISABLED, 210, 50,
            160, 35, hwnd, reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDC_BTN_PLAY)), hInst,
            nullptr);

        g_app.hBtnTranscribe = CreateWindowExW(
            0, L"BUTTON", L"Transcribe", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON | WS_DISABLED, 390,
            50, 160, 35, hwnd, reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDC_BTN_TRANSCRIBE)),
            hInst, nullptr);

        g_app.hOutput = CreateWindowExW(
            WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | WS_VSCROLL, 20,
            100, 540, 310, hwnd, reinterpret_cast<HMENU>(static_cast<INT_PTR>(IDC_OUTPUT)), hInst,
            nullptr);

        WPARAM fontParam = reinterpret_cast<WPARAM>(g_app.hFont);
        SendMessageW(g_app.hStatus, WM_SETFONT, fontParam, TRUE);
        SendMessageW(g_app.hBtnRecord, WM_SETFONT, fontParam, TRUE);
        SendMessageW(g_app.hBtnPlay, WM_SETFONT, fontParam, TRUE);
        SendMessageW(g_app.hBtnTranscribe, WM_SETFONT, fontParam, TRUE);
        SendMessageW(g_app.hOutput, WM_SETFONT, fontParam, TRUE);

        return 0;
    }

    case WM_COMMAND:
    {
        int id = LOWORD(wParam);

        if (id == IDC_BTN_RECORD)
        {
            if (g_app.state == AppState::Recording)
            {
                g_app.stopRecording = true;
                SetStatus(L"Stopping recording...");
            }
            else if (g_app.state == AppState::Idle)
            {
                g_app.state = AppState::Recording;
                g_app.stopRecording = false;
                SetStatus(L"Recording... (up to 5 seconds)");
                UpdateButtons();
                g_app.hThread = CreateThread(nullptr, 0, RecordThread, nullptr, 0, nullptr);
            }
        }
        else if (id == IDC_BTN_PLAY)
        {
            if (g_app.state == AppState::Idle && g_app.hasAudio)
            {
                g_app.state = AppState::Playing;
                SetStatus(L"Playing audio...");
                UpdateButtons();
                g_app.hThread = CreateThread(nullptr, 0, PlayThread, nullptr, 0, nullptr);
            }
        }
        else if (id == IDC_BTN_TRANSCRIBE)
        {
            if (g_app.state == AppState::Idle && g_app.hasAudio)
            {
                g_app.state = AppState::Transcribing;
                SetStatus(L"Transcribing...");
                SetWindowTextW(g_app.hOutput, L"");
                UpdateButtons();
                g_app.hThread = CreateThread(nullptr, 0, TranscribeThread, nullptr, 0, nullptr);
            }
        }

        return 0;
    }

    case WM_RECORDING_DONE:
    {
        if (g_app.hThread)
        {
            CloseHandle(g_app.hThread);
            g_app.hThread = nullptr;
        }

        g_app.state = AppState::Idle;

        if (wParam != 0)
        {
            wchar_t buf[128];
            float dur = static_cast<float>(g_app.audioSamples.size()) / kWhisperSampleRate;
            swprintf_s(buf, L"Recorded %.1f s. Ready to play or transcribe.", dur);
            SetStatus(buf);
        }
        else
        {
            SetStatus(L"Recording failed. Check microphone and try again.");
            g_app.hasAudio = false;
        }

        UpdateButtons();
        return 0;
    }

    case WM_PLAYBACK_DONE:
    {
        if (g_app.hThread)
        {
            CloseHandle(g_app.hThread);
            g_app.hThread = nullptr;
        }

        g_app.state = AppState::Idle;
        SetStatus(L"Playback complete. Ready.");
        UpdateButtons();
        return 0;
    }

    case WM_TRANSCRIBE_DONE:
    {
        if (g_app.hThread)
        {
            CloseHandle(g_app.hThread);
            g_app.hThread = nullptr;
        }

        g_app.state = AppState::Idle;

        wchar_t* result = reinterpret_cast<wchar_t*>(wParam);
        if (result)
        {
            SetWindowTextW(g_app.hOutput, result);
            free(result);
        }

        const auto resultStatus = static_cast<WhisperStopReason>(lParam);
        if (lParam == -1)
        {
            SetStatus(L"Transcription failed. Ready.");
        }
        else if (resultStatus == WhisperStopReason::EndOfTranscript)
        {
            SetStatus(L"Transcription complete (EOT). Ready.");
        }
        else if (resultStatus == WhisperStopReason::MaxTokens)
        {
            SetStatus(L"Transcription truncated at the token limit. Ready.");
        }
        else
        {
            SetStatus(L"Transcription truncated at decoder capacity. Ready.");
        }

        UpdateButtons();
        return 0;
    }

    case WM_STATUS_UPDATE:
    {
        wchar_t* text = reinterpret_cast<wchar_t*>(wParam);
        if (text)
        {
            SetStatus(text);
            free(text);
        }

        return 0;
    }

    case WM_CLOSE:
    {
        g_app.stopRecording = true;
        if (g_app.hThread)
        {
            WaitForSingleObject(g_app.hThread, 10000);
            CloseHandle(g_app.hThread);
            g_app.hThread = nullptr;
        }

        DestroyWindow(hwnd);
        return 0;
    }

    case WM_DESTROY:
    {
        if (g_app.hFont)
        {
            DeleteObject(g_app.hFont);
            g_app.hFont = nullptr;
        }

        PostQuitMessage(0);
        return 0;
    }
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

// Registers the window class, creates the fixed-size window, and pumps the
// message loop until the user closes it. The WinML objects in g_app must already
// be loaded and initialized before this is called.
static int RunWhisperUi()
{
    HINSTANCE hInst = GetModuleHandleW(nullptr);
    const wchar_t* className = L"WhisperSampleWnd";

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_BTNFACE + 1);
    wc.lpszClassName = className;
    wc.hIcon = LoadIconW(nullptr, IDI_APPLICATION);

    if (!RegisterClassExW(&wc))
    {
        wprintf(L"ERROR: RegisterClassEx failed.\n");
        return 1;
    }

    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    RECT rc = {0, 0, kClientW, kClientH};
    AdjustWindowRect(&rc, style, FALSE);

    g_app.hwnd = CreateWindowExW(0, className, L"Windows ML Runtime: Whisper speech-to-text", style,
                                 CW_USEDEFAULT, CW_USEDEFAULT, rc.right - rc.left,
                                 rc.bottom - rc.top, nullptr, nullptr, hInst, nullptr);

    if (!g_app.hwnd)
    {
        wprintf(L"ERROR: CreateWindow failed.\n");
        return 1;
    }

    ShowWindow(g_app.hwnd, SW_SHOW);
    UpdateWindow(g_app.hwnd);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    return static_cast<int>(msg.wParam);
}
