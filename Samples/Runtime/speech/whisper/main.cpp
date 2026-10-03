// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Whisper speech-to-text with separate encoder and decoder Runtime pipelines.
//
// Transcribe English speech from a WAV file, or with no WAV, from five seconds
// recorded in a small microphone window.
//
//   Encoder and decoder load as single-stage pipelines on the same target;
//   -Ep pins a provider, and an unavailable device fails with no fallback.
//   The encoder turns off two ORT optimizers through IWinMLOrtStageOptions.
//   The WAV becomes 16 kHz mono (up to 30 seconds), and its [1,80,3000]
//   log-mel features become a tensor on the target. The encoder runs once,
//   and its output is copied into one tensor that every decoder step reuses.
//   Decoding is greedy over a fixed [1,128] token buffer padded with
//   end-of-text: each step binds a new token tensor and the encoder state,
//   runs the full buffer (no KV cache), copies only the current logits row,
//   and takes the argmax until end-of-text or a full buffer.
//
// Run it
//   .\run_whisper.ps1 [-WavPath <wav>] [-Device cpu|gpu|npu] [-Ep <name>]
//                     [-Performance|-Efficiency] [-Diagnostics]
//   whisper-speech-to-text.exe [wav-file] [--device cpu|gpu|npu] [--ep <name>]
//                              [--performance|--efficiency] [--verbose]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Runtime/tutorials/04-speech-to-language.md
//   ../../../../docs/Runtime/tutorials/05-accelerators.md
//   ../../../../docs/Runtime/providers.md
//   ../../../../docs/api-reference/IWinMLRawTensorFactory.md

#include <cstdint>
#include <cstdio>
#include <functional>
#include <string>
#include <unordered_map>
#include <vector>

#include <Windows.h>

#include <WinMLRuntime.h>
#include <WinMLRuntimeOrt.h>

#include "common.h"
#include "audio_utils.h"
#include "ep_catalog_utils.h"
#include "execution_target_utils.h"
#include "llm_inference.h"
#include "tensor_lock_utils.h"

#include "whisper_tokenizer.h"
#include "whisper_app_ui.h"

// Creates a one-stage model pipeline. Build materializes the stage on the target
// and validates that the selected backend can run this model there.
static HRESULT BuildSingleStagePipeline(IWinMLRuntime* runtime, IWinMLExecutionTarget* target,
                                        IWinMLModel* model, LPCWSTR debugName,
                                        ComPtr<IWinMLPipeline>& pipeline,
                                        ComPtr<IWinMLStage>& stage,
                                        bool disableLayoutTransform = false)
{
    ComPtr<IWinMLPipelineBuilder> builder;
    CHECK_HR(runtime->CreatePipelineBuilder(builder.GetAddressOf()));
    CHECK_HR(builder->AddModelStage(model, target, debugName, stage.GetAddressOf()));
    CHECK_HR(stage->RequestOutput(0));

    // The encoder's two convolutions are 1-D. Two ONNX Runtime graph
    // optimizations do not handle them on every target: the channels-last layout
    // transform can rewrite them into a fused kernel that accepts only 2-D
    // kernels, and Conv activation fusion attaches an activation that some GPU
    // kernels reject. Disable just those two passes rather than lowering the
    // whole optimization level.
    if (disableLayoutTransform)
    {
        ComPtr<IWinMLOrtStageOptions> ortStageOptions;
        if (SUCCEEDED(stage->QueryInterface(IID_PPV_ARGS(ortStageOptions.GetAddressOf()))))
        {
            CHECK_HR(
                ortStageOptions->SetSessionConfigEntry(L"optimization.disable_specified_optimizers",
                                                       L"NhwcTransformer;ConvActivationFusion"));
        }
    }

    CHECK_HR(builder->Build(pipeline.GetAddressOf()));
    return S_OK;
}

static std::wstring FindWhisperAsset(const wchar_t* filename)
{
    wchar_t configuredDirectory[MAX_PATH + 1] = {};
    const DWORD configuredLength = GetEnvironmentVariableW(
        L"WINML_WHISPER_MODEL_DIR", configuredDirectory, ARRAYSIZE(configuredDirectory));
    if (configuredLength > 0 && configuredLength < ARRAYSIZE(configuredDirectory))
    {
        const std::wstring configuredPath = std::wstring(configuredDirectory) + L"\\" + filename;
        if (FileExists(configuredPath))
        {
            return configuredPath;
        }
    }

    const std::wstring preparedPath =
        FindModelPath((std::wstring(L"whisper-medium-q4f16\\onnx\\") + filename).c_str());
    if (!preparedPath.empty())
    {
        return preparedPath;
    }

    const std::wstring adjacentPath = GetExecutableDirectory() + L"\\" + filename;
    return FileExists(adjacentPath) ? adjacentPath : std::wstring();
}

static HRESULT LoadWhisper(const DeviceArgs& device, ComPtr<IWinMLRuntime>& runtime,
                           ComPtr<IWinMLExecutionTarget>& target, ComPtr<IWinMLModel>& encoderModel,
                           ComPtr<IWinMLModel>& decoderModel,
                           ComPtr<IWinMLPipeline>& encoderPipeline,
                           ComPtr<IWinMLPipeline>& decoderPipeline,
                           ComPtr<IWinMLStage>& encoderStage, ComPtr<IWinMLStage>& decoderStage,
                           std::unordered_map<int64_t, std::string>& vocab)
{
    // The Runtime is the root factory for execution targets, models, and
    // pipeline builders used by both the console and UI paths.
    CHECK_HR(WinMLCreateRuntime(__uuidof(IWinMLRuntime),
                                reinterpret_cast<void**>(runtime.GetAddressOf())));

    std::wstring encPath = FindWhisperAsset(L"encoder_model.onnx");
    std::wstring decPath = FindWhisperAsset(L"decoder_model.onnx");
    if (encPath.empty() || decPath.empty())
    {
        wprintf(L"ERROR: Could not find the Whisper model assets.\n");
        wprintf(L"Run .\\check_artifacts.ps1 -Sample whisper, or use .\\run_whisper.ps1.\n");
        wprintf(L"You can also set WINML_WHISPER_MODEL_DIR to a folder containing\n");
        wprintf(L"encoder_model.onnx, decoder_model.onnx, and vocab.json.\n");
        return E_FAIL;
    }

    std::wstring vocPath = SiblingPath(encPath, L"vocab.json");
    if (!FileExists(vocPath))
    {
        vocPath = FindWhisperAsset(L"vocab.json");
    }

    vocab = LoadVocab(vocPath.c_str());
    if (vocab.empty())
    {
        wprintf(L"ERROR: Could not load vocab.json.\n");
        return E_FAIL;
    }

    wprintf(L"  Encoder: %s\n", encPath.c_str());
    wprintf(L"  Decoder: %s\n", decPath.c_str());
    wprintf(L"  Vocab: %s (%zu tokens)\n", vocPath.c_str(), vocab.size());

    // Create the requested stage target. When --ep is present, the helper has
    // registered the provider through the catalog and pins this target to it.
    ComPtr<IWinMLExecutionTarget> pipelineTarget;
    CHECK_HR(CreateExecutionTarget(runtime.Get(), device, pipelineTarget.GetAddressOf(),
                                   target.GetAddressOf()));

    // LoadModelFromFile reads each prepared ONNX artifact; schemas are checked
    // before the decoder loop relies on fixed ordinals and shapes.
    CHECK_HR(runtime->LoadModelFromFile(encPath.c_str(), nullptr, encoderModel.GetAddressOf()));
    CHECK_HR(runtime->LoadModelFromFile(decPath.c_str(), nullptr, decoderModel.GetAddressOf()));

    // Both pipelines use the same placement request. The returned tensor target is
    // the underlying hardware target used for raw tensor allocation.
    CHECK_HR(BuildSingleStagePipeline(runtime.Get(), pipelineTarget.Get(), encoderModel.Get(),
                                      L"whisper-encoder", encoderPipeline, encoderStage,
                                      /*disableLayoutTransform*/ true));
    CHECK_HR(BuildSingleStagePipeline(runtime.Get(), pipelineTarget.Get(), decoderModel.Get(),
                                      L"whisper-decoder", decoderPipeline, decoderStage));

    ComPtr<IWinMLStageSchema> decoderSchema;
    CHECK_HR(decoderStage->QueryInterface(IID_PPV_ARGS(decoderSchema.GetAddressOf())));
    WINML_TENSOR_DESC tokenDesc = {};
    CHECK_HR(decoderSchema->GetInputTensorDesc(0, &tokenDesc));
    CHECK_HR_IF(tokenDesc.dimensionCount != 2 || tokenDesc.dimensions == nullptr ||
                    tokenDesc.dimensions[0] != 1 || tokenDesc.dimensions[1] != kMaxDecodeTokens,
                HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
    return S_OK;
}

// Core inference: mel spectrogram -> encoder -> autoregressive decoder loop ->
// text. The pipelines must already be initialized. Status messages and decoded
// tokens are surfaced through callbacks so the console and UI front-ends share
// this one implementation; in UI mode this runs on a worker thread, so the
// callbacks must not touch UI controls directly.
HRESULT Transcribe(IWinMLExecutionTarget* target, IWinMLPipeline* encoderPipeline,
                   IWinMLStage* encoderStage, IWinMLPipeline* decoderPipeline,
                   IWinMLStage* decoderStage, const std::unordered_map<int64_t, std::string>& vocab,
                   const float* samples, size_t sampleCount, uint32_t sampleRate,
                   const std::function<void(const wchar_t*)>& onStatus,
                   const std::function<void(const std::string&)>& onToken,
                   std::vector<int64_t>& generatedTokensOut, std::string& transcriptOut,
                   WhisperStopReason& stopReasonOut)
{
    generatedTokensOut.clear();
    transcriptOut.clear();
    stopReasonOut = WhisperStopReason::Capacity;
    onStatus(L"Computing mel spectrogram...");
    MelSpectrogramData mel = ComputeMelSpectrogram(samples, sampleCount, sampleRate);

    onStatus(L"Running encoder...");
    // CreateTensorOnTarget wraps IWinMLRawTensorFactory::CreateTensor on the
    // target so the mel buffer is allocated where the encoder input should live.
    UINT64 melDims[] = {1, kWhisperMelBins, kWhisperTimeFrames};
    WINML_TENSOR_DESC melDesc = {};
    melDesc.dataType = WINML_TENSOR_DATA_TYPE_FLOAT32;
    melDesc.dimensionCount = 3;
    melDesc.dimensions = melDims;
    ComPtr<IWinMLTensor> melTensor;
    CHECK_HR(CreateTensorOnTarget(target, &melDesc, mel.data.data(),
                                  static_cast<UINT64>(mel.data.size() * sizeof(float)),
                                  melTensor.GetAddressOf()));

    // Inputs are bound positionally. The encoder has a single input (index 0):
    // the mel spectrogram.
    CHECK_HR(encoderStage->BindInput(0, melTensor.Get()));
    CHECK_HR(encoderPipeline->Run());

    ComPtr<IWinMLTensor> encOutTensor;
    CHECK_HR(encoderStage->GetOutput(0, encOutTensor.GetAddressOf()));

    // A synchronized-capable lock lets this code read the encoder output whether
    // it is CPU-resident or device-resident.
    LockedTensorData encOutputData;
    CHECK_HR(LockTensorForReadAny(encOutTensor.Get(), &encOutputData));
    CHECK_HR_IF(encOutputData.size == 0 || encOutputData.data == nullptr, E_FAIL);

    // Copy encoder output into a fresh tensor whose lifetime is independent of the
    // encoder stage output object while it feeds the decoder.
    WINML_TENSOR_DESC encOutDesc = {};
    CHECK_HR(encOutTensor->GetDesc(&encOutDesc));
    CHECK_HR_IF(encOutDesc.dimensionCount == 0, E_FAIL);

    size_t expectedBytes = sizeof(float);
    for (UINT32 d = 0; d < encOutDesc.dimensionCount; ++d)
    {
        expectedBytes *= static_cast<size_t>(encOutDesc.dimensions[d]);
    }

    CHECK_HR_IF(encOutputData.size < expectedBytes, E_FAIL);

    ComPtr<IWinMLTensor> encoderState;
    CHECK_HR(CreateTensorOnTarget(target, &encOutDesc, encOutputData.data, encOutputData.size,
                                  encoderState.GetAddressOf()));

    onStatus(L"Decoding...");
    // The decoder has two positional inputs: index 0 = the token ids, index 1 =
    // the encoder hidden state.
    constexpr UINT32 decTokenIdx = 0;
    constexpr UINT32 decStateIdx = 1;

    // The decoder is primed for English transcription without timestamps.
    std::vector<int64_t> tokens = {kSOT, kEnglish, kTranscribe, kNoTimestamps};

    // A single fixed-length [1, kMaxDecodeTokens] token buffer drives the decode
    // loop. Only the first tokens.size() slots hold real tokens; the rest are
    // padding. Causal self-attention means the logits at the current position
    // depend only on the tokens at or before it, so the padding never affects the
    // prediction we read. The fixed input shape matches the prepared decoder model.
    std::vector<int64_t> tokenBuffer(kMaxDecodeTokens, kEOT);
    const UINT64 tokDims[] = {1, static_cast<UINT64>(kMaxDecodeTokens)};
    WhisperTokenDecoder textDecoder(vocab);
    std::string transcript;

    while (tokens.size() < kMaxDecodeTokens)
    {
        std::copy(tokens.begin(), tokens.end(), tokenBuffer.begin());

        // Recreate the fixed-shape token tensor for this step; only the prefix
        // through the current position is meaningful to the decoder.
        WINML_TENSOR_DESC tokDesc = {};
        tokDesc.dataType = WINML_TENSOR_DATA_TYPE_INT64;
        tokDesc.dimensionCount = 2;
        tokDesc.dimensions = tokDims;
        ComPtr<IWinMLTensor> tokTensor;
        CHECK_HR(CreateTensorOnTarget(target, &tokDesc, tokenBuffer.data(),
                                      static_cast<UINT64>(tokenBuffer.size() * sizeof(int64_t)),
                                      tokTensor.GetAddressOf()));

        CHECK_HR(decoderStage->BindInput(decTokenIdx, tokTensor.Get()));
        CHECK_HR(decoderStage->BindInput(decStateIdx, encoderState.Get()));
        CHECK_HR(decoderPipeline->Run());

        ComPtr<IWinMLTensor> decOutTensor;
        CHECK_HR(decoderStage->GetOutput(0, decOutTensor.GetAddressOf()));

        const size_t currentPos = tokens.size() - 1;
        // ReadFloat32LogitsAtPosition copies only the [1,1,vocab] slice needed
        // to choose the next token, not the full decoder output.
        std::vector<float> positionLogits;
        CHECK_HR(ReadFloat32LogitsAtPosition(target, decOutTensor.Get(),
                                             static_cast<UINT32>(currentPos), kVocabSize,
                                             positionLogits));
        const int64_t next = ArgmaxToken(positionLogits);
        if (next == kEOT)
        {
            stopReasonOut = WhisperStopReason::EndOfTranscript;
            break;
        }

        const std::string fragment = textDecoder.DecodeToken(next);
        transcript += fragment;
        if (!fragment.empty())
        {
            onToken(fragment);
        }

        generatedTokensOut.push_back(next);
        tokens.push_back(next);
    }

    const std::string finalFragment = textDecoder.Finish();
    transcript += finalFragment;
    if (!finalFragment.empty())
    {
        onToken(finalFragment);
    }

    if (!transcript.empty() && transcript.front() == ' ')
    {
        transcript.erase(transcript.begin());
    }

    transcriptOut = std::move(transcript);
    return S_OK;
}

// Console-only WAV transcription (no UI).
static int TranscribeConsole(const wchar_t* wavPath, const DeviceArgs& device)
{
    wprintf(L"=== Windows ML Runtime: Whisper speech-to-text ===\n\n");
    wprintf(L"Device: %s\n", DeviceTypeName(device.deviceType));
    wprintf(L"Loading models...\n");

    ComPtr<IWinMLRuntime> runtime;
    ComPtr<IWinMLExecutionTarget> target;
    ComPtr<IWinMLModel> encoderModel, decoderModel;
    ComPtr<IWinMLPipeline> encoderPipeline, decoderPipeline;
    ComPtr<IWinMLStage> encoderStage, decoderStage;
    std::unordered_map<int64_t, std::string> vocab;
    CHECK_HR(LoadWhisper(device, runtime, target, encoderModel, decoderModel, encoderPipeline,
                         decoderPipeline, encoderStage, decoderStage, vocab));

    wprintf(L"\nLoading: %s\n", wavPath);
    WavData wav;
    CHECK_HR(LoadWavFile(wavPath, wav));
    wprintf(L"  %.1f s audio\n", static_cast<float>(wav.samples.size()) / wav.sampleRate);
    if (wav.truncated)
    {
        wprintf(L"  Input was limited to Whisper's 30-second window.\n");
    }

    bool firstToken = true;
    std::vector<int64_t> generatedTokens;
    std::string transcript;
    WhisperStopReason stopReason = WhisperStopReason::Capacity;
    CHECK_HR(Transcribe(
        target.Get(), encoderPipeline.Get(), encoderStage.Get(), decoderPipeline.Get(),
        decoderStage.Get(), vocab, wav.samples.data(), wav.samples.size(), wav.sampleRate,
        [](const wchar_t* status) {
            wprintf(L"\n%s\n", status);
        },
        [&firstToken](const std::string& token) {
            if (firstToken)
            {
                wprintf(L"\nTranscription:\n\n  >> ");
                firstToken = false;
            }

            wprintf(L"%s", Utf8ToWide(token).c_str());
            fflush(stdout);
        },
        generatedTokens, transcript, stopReason));

    wprintf(L"\n\nToken IDs:");
    for (int64_t token : generatedTokens)
    {
        wprintf(L" %lld", static_cast<long long>(token));
    }

    if (stopReason == WhisperStopReason::EndOfTranscript)
    {
        wprintf(L"\n\n=== Transcription complete (EOT). ===\n");
    }
    else
    {
        wprintf(L"\n\n=== Transcription truncated at %s. ===\n", WhisperStopReasonText(stopReason));
    }

    return 0;
}

int wmain(int argc, wchar_t* argv[])
{
    setvbuf(stdout, nullptr, _IONBF, 0);

    DeviceArgs device = ParseDeviceArgs(argc, argv);

    // Parse --ep and the optional WAV file path.
    const wchar_t* wavPath = nullptr;
    std::string epFilter;
    for (int i = 1; i < argc; ++i)
    {
        if (wcscmp(argv[i], L"--help") == 0 || wcscmp(argv[i], L"-h") == 0)
        {
            wprintf(L"Usage: %s [wav-file] [options]\n\n", argv[0]);
            wprintf(L"  [wav-file]             Path to a WAV file for console transcription.\n");
            wprintf(L"                         Omit for interactive UI mode.\n\n");
            wprintf(L"  --device cpu|gpu|npu   Execution device (default: cpu)\n");
            wprintf(L"  --ep NAME             Pin one installed execution provider\n");
            wprintf(L"  --performance         Prefer a discrete GPU adapter\n");
            wprintf(L"  --efficiency          Prefer an integrated GPU adapter\n");
            wprintf(L"  --verbose             Enable verbose Runtime logging (node placement)\n");
            wprintf(L"  --help, -h            Show this help message\n");
            return 0;
        }

        if (wcscmp(argv[i], L"--device") == 0)
        {
            ++i;
            continue;
        }

        if (wcscmp(argv[i], L"--ep") == 0 && i + 1 < argc)
        {
            ++i;
            epFilter = WideToUtf8(argv[i]);
            continue;
        }

        if (wcscmp(argv[i], L"--performance") == 0 || wcscmp(argv[i], L"--efficiency") == 0)
        {
            continue;
        }

        if (wcscmp(argv[i], L"--verbose") == 0)
        {
            // Raise verbose logging before the Runtime initializes so node-placement
            // logs reveal which execution provider served each node.
            EnableVerboseSampleLogging();
            continue;
        }

        if (wcsncmp(argv[i], L"--", 2) == 0)
        {
            wprintf(L"ERROR: Unknown option '%s'.\n", argv[i]);
            return 1;
        }

        if (!wavPath)
        {
            wavPath = argv[i];
        }
    }

    // Register a requested provider before target creation. The catalog helper
    // verifies the requested device class so placement failures are explicit.
    if (!epFilter.empty() || device.deviceType == WINML_EXECUTION_TARGET_KIND_NPU)
    {
        if (FAILED(PrepareAndValidateExecutionProviders(
                device.deviceType, epFilter.empty() ? nullptr : epFilter.c_str())))
        {
            return 1;
        }
    }

    // A WAV argument runs console-only transcription; otherwise launch the UI.
    if (wavPath)
    {
        return TranscribeConsole(wavPath, device);
    }

    wprintf(L"=== Windows ML Runtime: Whisper speech-to-text ===\n\n");
    wprintf(L"Device: %s\n", DeviceTypeName(device.deviceType));
    wprintf(L"Loading models...\n");

    CHECK_HR(LoadWhisper(device, g_app.runtime, g_app.target, g_app.encoderModel,
                         g_app.decoderModel, g_app.encoderPipeline, g_app.decoderPipeline,
                         g_app.encoderStage, g_app.decoderStage, g_app.vocab));

    wprintf(L"UI ready.\n");
    return RunWhisperUi();
}
