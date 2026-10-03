// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Text generation: stream completion text from a Windows ML Task session.
//
// Continue a prompt, or answer it as a chat turn with -Chat, and stream the
// text from an ONNX/ORT model, a GGUF model, or a prepared hybrid directory.
//
//   The app builds the Runtime pipeline and tokenizer on one target (CPU, or
//   GPU for GGUF with -Device gpu); the Task runs the generation loop. An
//   ONNX/ORT stage declares a past/present state pair and capacity, while a
//   GGUF model keeps its own state and tokenizer. With no sampling flags the
//   sample uses temperature 0.7, top-p 0.9, and repetition penalty 1.1;
//   -Greedy turns sampling off. GGUF speculative decoding verifies drafted
//   tokens in one step, drafting from the model's own predictor, a smaller
//   draft model, or n-grams of earlier text.
//   GenerateText (or GenerateTokens for -Chat) takes a cancellation source;
//   ReadNext yields fragments until completion, and the streamed text must
//   match GetResult. hybrid-ort prefills and decodes with two ORT CPU
//   pipelines; the app owns their packed KV state, copying it to decode
//   after the first token and from decode output to input after each later
//   token.
//
// Run it
//   ..\..\run_text_generation.ps1 [-Backend ort|llama|hybrid-ort]
//                                  [-Prompt <text>] [-MaxNewTokens <count>]
//                                  [-Chat] [-Greedy] [-Temperature <value>]
//                                  [-TopP <value>] [-TopK <count>]
//                                  [-RepetitionPenalty <value>] [-Seed <value>]
//                                  [-Device cpu|gpu]
//                                  [-Speculative none|model|draft-model|prompt-lookup]
//                                  [-DraftTokens <count>] [-DraftModel <path>]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Tasks/task-lifecycle.md
//   ../../../../docs/api-reference/IWinMLTasks.md
//   ../../../../docs/api-reference/IWinMLTextGenerationTask.md

#include "text_generation_task.h"
#include "hybrid_text_generation_task.h"

#include <cstdio>
#include <cwchar>
#include <filesystem>
#include <vector>

int wmain(int argumentCount, wchar_t** arguments)
try
{
    using namespace winmlsamples::tasks;

    // The executable keeps the Task flow the same for ONNX Runtime and
    // llama.cpp models.
    if (argumentCount < 3)
    {
        std::fwprintf(stderr, L"Usage: task-text-generation.exe <model> "
                              L"<tokenizer-source|-> [prompt] [max-new-tokens] "
                              L"[ort|llama|hybrid-ort]\n"
                              L"  hybrid-ort takes a prepared model directory and prefills the "
                              L"prompt with ORT while decoding each token as a separate "
                              L"pipeline.\n"
                              L"Options: --chat --greedy --temperature T --top-k K --top-p P "
                              L"--repetition-penalty R --seed S\n"
                              L"  --device cpu|gpu          llama backend placement (default cpu)\n"
                              L"  --speculative none|model|draft-model|prompt-lookup\n"
                              L"                            propose several tokens per step and "
                              L"verify them in one pass (GGUF)\n"
                              L"  --draft-tokens N          draft tokens per step, 1..16 "
                              L"(default 3)\n"
                              L"  --draft-model <gguf>      model: optional DFlash/DFlash2/DSpark "
                              L"companion; draft-model: required smaller model\n");
        return 2;
    }

    LPCWSTR tokenizerSource = std::wcscmp(arguments[2], L"-") == 0 ? nullptr : arguments[2];
    LPCWSTR prompt = argumentCount >= 4 ? arguments[3] : L"The sky is often";
    UINT32 maxNewTokens = 32;
    SamplingArguments sampling;
    SpeculativeArguments speculative;
    bool useGpu = false;
    bool forceGreedy = false;
    bool chat = false;
    // Optional sampling flags follow the positional arguments.
    int positionalCount = argumentCount;
    for (int index = 4; index < argumentCount; ++index)
    {
        const std::wstring flag = arguments[index];
        if (flag.rfind(L"--", 0) != 0)
        {
            continue;
        }

        if (positionalCount > index)
        {
            positionalCount = index;
        }

        if (flag == L"--greedy")
        {
            forceGreedy = true;
            continue;
        }

        if (flag == L"--chat")
        {
            chat = true;
            continue;
        }

        if (index + 1 >= argumentCount)
        {
            std::fwprintf(stderr, L"%ls needs a value.\n", flag.c_str());
            return 2;
        }

        LPCWSTR value = arguments[++index];
        bool parsed = true;
        if (flag == L"--temperature")
        {
            sampling.hasTemperature = true;
            parsed = ParseFloatOption(value, 0.0f, 5.0f, sampling.temperature);
        }
        else if (flag == L"--top-k")
        {
            sampling.hasTopK = true;
            parsed = ParseUInt32Option(value, 100000, sampling.topK);
        }
        else if (flag == L"--top-p")
        {
            sampling.hasTopP = true;
            parsed = ParseFloatOption(value, 0.0f, 1.0f, sampling.topP);
        }
        else if (flag == L"--repetition-penalty")
        {
            sampling.hasRepetitionPenalty = true;
            parsed = ParseFloatOption(value, 0.0f, 5.0f, sampling.repetitionPenalty);
        }
        else if (flag == L"--seed")
        {
            sampling.hasSeed = true;
            parsed = ParseUInt64Option(value, sampling.seed);
        }
        else if (flag == L"--device")
        {
            const std::wstring device = value;
            useGpu = device == L"gpu";
            parsed = useGpu || device == L"cpu";
        }
        else if (flag == L"--speculative")
        {
            const std::wstring method = value;
            if (method == L"none")
            {
                speculative.method = WINML_TEXT_GENERATION_SPECULATIVE_METHOD_NONE;
            }
            else if (method == L"model")
            {
                // The model's own draft predictor: built-in MTP/NextN layers,
                // or a block draft companion named by --draft-model.
                speculative.method = WINML_TEXT_GENERATION_SPECULATIVE_METHOD_MODEL;
            }
            else if (method == L"draft-model")
            {
                speculative.method = WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL;
            }
            else if (method == L"prompt-lookup")
            {
                speculative.method = WINML_TEXT_GENERATION_SPECULATIVE_METHOD_PROMPT_LOOKUP;
            }
            else
            {
                parsed = false;
            }
        }
        else if (flag == L"--draft-tokens")
        {
            parsed = ParseUInt32Option(value, 16, speculative.draftTokenCount) &&
                     speculative.draftTokenCount != 0;
        }
        else if (flag == L"--draft-model")
        {
            speculative.draftModelPath = value;
        }
        else
        {
            std::fwprintf(stderr, L"Unknown option: %ls\n", flag.c_str());
            return 2;
        }

        if (!parsed)
        {
            std::fwprintf(stderr, L"%ls requires a valid value; got '%ls'.\n", flag.c_str(), value);
            return 2;
        }
    }

    // Greedy selection makes a small model repeat itself once a continuation
    // runs past a sentence or two, so the sample samples by default and keeps
    // --greedy for repeatable output.
    const bool sampledByDefault = !forceGreedy && !sampling.hasTemperature && !sampling.hasTopK &&
                                  !sampling.hasTopP && !sampling.hasRepetitionPenalty;
    if (sampledByDefault)
    {
        sampling.hasTemperature = true;
        sampling.temperature = 0.7f;
        sampling.hasTopP = true;
        sampling.topP = 0.9f;
        sampling.hasRepetitionPenalty = true;
        sampling.repetitionPenalty = 1.1f;
    }

    if (forceGreedy)
    {
        sampling = SamplingArguments{};
    }

    if (sampling.hasTemperature || sampling.hasTopP || sampling.hasTopK ||
        sampling.hasRepetitionPenalty)
    {
        std::wprintf(L"Decoding: sampled (");
        LPCWSTR separator = L"";
        if (sampling.hasTemperature)
        {
            std::wprintf(L"%lstemperature=%.2f", separator, sampling.temperature);
            separator = L" ";
        }

        if (sampling.hasTopP)
        {
            std::wprintf(L"%lstop-p=%.2f", separator, sampling.topP);
            separator = L" ";
        }

        if (sampling.hasTopK)
        {
            std::wprintf(L"%lstop-k=%u", separator, sampling.topK);
            separator = L" ";
        }

        if (sampling.hasRepetitionPenalty)
        {
            std::wprintf(L"%lsrepetition-penalty=%.2f", separator, sampling.repetitionPenalty);
            separator = L" ";
        }

        if (sampling.hasSeed)
        {
            std::wprintf(L"%lsseed=%llu", separator, sampling.seed);
        }

        std::wprintf(L")%ls\n", sampledByDefault
                                    ? L" by default; pass -Greedy for deterministic output"
                                    : L"");
    }
    else
    {
        std::wprintf(L"Decoding: greedy (deterministic)\n");
    }

    if (positionalCount >= 5 && !ParsePositiveTokenCount(arguments[4], maxNewTokens))
    {
        std::fwprintf(stderr, L"max-new-tokens must be a positive 32-bit integer.\n");
        return 2;
    }

    const std::wstring backend =
        positionalCount >= 6 ? arguments[5] : (tokenizerSource == nullptr ? L"llama" : L"ort");
    const bool isHybrid = backend == L"hybrid-ort";
    if (backend != L"ort" && backend != L"llama" && !isHybrid)
    {
        std::fwprintf(stderr, L"backend must be ort, llama, or hybrid-ort.\n");
        return 2;
    }

    const std::wstring modelExtension = GetLowercaseExtension(arguments[1]);
    if (backend == L"llama" && modelExtension != L".gguf")
    {
        std::fwprintf(stderr, L"llama requires a GGUF model.\n");
        return 2;
    }

    if (backend == L"ort" && modelExtension != L".onnx" && modelExtension != L".ort")
    {
        std::fwprintf(stderr, L"ort requires an ONNX or ORT model.\n");
        return 2;
    }

    if (speculative.method != WINML_TEXT_GENERATION_SPECULATIVE_METHOD_NONE && backend != L"llama")
    {
        std::fwprintf(stderr, L"--speculative requires a GGUF model on the llama backend.\n");
        return 2;
    }

    if (speculative.method == WINML_TEXT_GENERATION_SPECULATIVE_METHOD_MODEL)
    {
        // For the model method, --draft-model names a companion block draft
        // that replaces the built-in layers.
        speculative.draftPredictorPath = std::move(speculative.draftModelPath);
        speculative.draftModelPath.clear();
    }

    if ((speculative.method == WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL) !=
        !speculative.draftModelPath.empty())
    {
        std::fwprintf(stderr, L"--draft-model is required by --speculative draft-model, optional "
                              L"with model, and not used otherwise.\n");
        return 2;
    }

    if (useGpu && backend != L"llama")
    {
        std::fwprintf(stderr, L"--device applies to the llama backend.\n");
        return 2;
    }

    if (isHybrid && !std::filesystem::is_directory(std::filesystem::path(arguments[1])))
    {
        std::fwprintf(stderr, L"the hybrid backend requires a prepared model directory.\n");
        return 2;
    }

    // The Runtime owns the low-level objects supplied to the Task composition.
    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    if (isHybrid)
    {
        const std::filesystem::path modelDirectory{arguments[1]};
        HybridTextGenerationObjects hybrid;
        // Hybrid mode configures the Text Generation Task's prefill/decode
        // profile from two caller-built Runtime pipelines in one model folder.
        THROW_IF_FAILED(BuildHybridTextGeneration(runtime.Get(),
                                                  (modelDirectory / L"task_model.onnx").c_str(),
                                                  (modelDirectory / L"task_decode.onnx").c_str(),
                                                  tokenizerSource, maxNewTokens, sampling, hybrid));
        THROW_IF_FAILED(VerifyExactRuntime(hybrid.task.session.Get(), runtime.Get()));
        // Cancellation is supplied when generation starts; closing the pull
        // stream still owns stream lifetime.
        ComPtr<IWinMLCancellationSource> hybridCancellation;
        THROW_IF_FAILED(
            hybrid.task.tasks->CreateCancellationSource(hybridCancellation.GetAddressOf()));
        std::wprintf(L"Prefill placement: ORT CPU\nDecode placement: %ls\n",
                     hybrid.decodePlacement.c_str());
        // The prefill output is sized from the prompt tokens, so the prompt is
        // encoded up front: as plain text, or with -Chat as a chat turn.
        std::vector<UINT32> promptTokens;
        THROW_IF_FAILED(EncodePrompt(hybrid.tokenizer.Get(), prompt, chat, promptTokens));
        TextGenerationResult hybridGeneration;
        // GenerateHybridText reads streamed fragments and then checks the final
        // result for text, token IDs, finish reason, and terminal error status.
        std::wprintf(L"Generated text: ");
        THROW_IF_FAILED(GenerateHybridText(hybrid, hybridCancellation.Get(), promptTokens, true,
                                           hybridGeneration));
        std::wprintf(L"Token counts: prompt=%u generated=%u\n", hybridGeneration.promptTokenCount,
                     hybridGeneration.generatedTokenCount);
        std::wprintf(L"Finished because: %ls\n",
                     DescribeFinishReason(hybridGeneration.finishReason));
        PrintFinishReasonGuidance(hybridGeneration.finishReason, maxNewTokens,
                                  hybridGeneration.generatedTokenCount, chat);
        std::wprintf(L"Timing: first=%.2fms total=%.2fms\n",
                     hybridGeneration.timeToFirstTokenMilliseconds,
                     hybridGeneration.totalMilliseconds);
        return 0;
    }

    // The non-hybrid sample uses one target for model stages and token tensors:
    // the CPU, or the GPU when --device gpu places a llama model there. The
    // Runtime API is where placement is selected.
    ComPtr<IWinMLExecutionTarget> target;
    TextGenerationObjects objects;
    if (useGpu)
    {
        THROW_IF_FAILED(runtime->CreateExecutionTarget(
            WINML_EXECUTION_TARGET_KIND_GPU, WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE,
            target.GetAddressOf()));
    }
    else
    {
        THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(target.GetAddressOf()));
    }

    // BuildTextGeneration (shared/text_generation_task.h) loads the artifact,
    // builds a Runtime pipeline, creates IWinMLTasks, validates the typed Text
    // Generation configuration, and creates the default session.
    THROW_IF_FAILED(BuildTextGeneration(runtime.Get(), target.Get(), arguments[1], tokenizerSource,
                                        maxNewTokens, sampling, objects, speculative));
    IWinMLTextGenerationSession* session = objects.task.session.Get();
    IWinMLTextGenerationOptions* options = objects.task.options.Get();
    IWinMLTasks* tasks = objects.task.tasks.Get();
    const UINT64 effectiveSequenceCapacity = objects.effectiveSequenceCapacity;
    const WINML_SEQUENCE_CAPACITY_DISCLOSURE declaredCapacityDisclosure =
        objects.declaredCapacityDisclosure;
    const UINT64 declaredSequenceCapacity = objects.declaredSequenceCapacity;
    THROW_IF_FAILED(VerifyExactRuntime(session, runtime.Get()));

    // IWinMLTasks creates the cancellation source accepted by Task operations.
    ComPtr<IWinMLCancellationSource> cancellation;
    THROW_IF_FAILED(tasks->CreateCancellationSource(cancellation.GetAddressOf()));

    TextGenerationResult generation;
    std::wprintf(L"Generated text: ");
    if (chat)
    {
        // The Task's tokenizer applies the model's chat template, and
        // GenerateTokens starts generation from the formatted prompt.
        std::vector<UINT32> promptTokens;
        THROW_IF_FAILED(EncodeChatPrompt(objects.task.tokenizer.Get(), prompt, promptTokens));
        THROW_IF_FAILED(
            GenerateTokens(session, options, promptTokens, cancellation.Get(), true, generation));
    }
    else
    {
        // GenerateText encodes the prompt, drains the pull stream, and reads the
        // terminal result before the stream is closed.
        THROW_IF_FAILED(
            GenerateText(session, options, prompt, cancellation.Get(), true, generation));
    }

    std::wprintf(L"\n");
    std::wprintf(L"Token counts: prompt=%u generated=%u\n", generation.promptTokenCount,
                 generation.generatedTokenCount);
    std::wprintf(L"Finished because: %ls\n", DescribeFinishReason(generation.finishReason));
    PrintFinishReasonGuidance(generation.finishReason, maxNewTokens, generation.generatedTokenCount,
                              chat);
    std::wprintf(L"Timing: first=%.2fms total=%.2fms\n", generation.timeToFirstTokenMilliseconds,
                 generation.totalMilliseconds);
    const double decodeMilliseconds =
        generation.totalMilliseconds - generation.timeToFirstTokenMilliseconds;
    if (generation.generatedTokenCount > 1 && decodeMilliseconds > 0.0)
    {
        std::wprintf(L"Decode rate: %.2f tokens/s\n",
                     (generation.generatedTokenCount - 1) * 1000.0 / decodeMilliseconds);
    }

    // The Task result reports how many draft tokens each verification step
    // proposed and kept; acceptance drives the speedup over sequential decoding.
    if (speculative.method != WINML_TEXT_GENERATION_SPECULATIVE_METHOD_NONE)
    {
        const auto& statistics = generation.speculative;
        std::wprintf(L"Speculative decoding: %ls steps=%u drafted=%u accepted=%u",
                     DescribeSpeculativeMethod(statistics.method), statistics.verificationStepCount,
                     statistics.draftTokenCount, statistics.acceptedDraftTokenCount);
        if (statistics.draftTokenCount != 0)
        {
            std::wprintf(L" acceptance=%.1f%%",
                         100.0 * statistics.acceptedDraftTokenCount / statistics.draftTokenCount);
        }

        std::wprintf(L"\n");
        if (statistics.method == WINML_TEXT_GENERATION_SPECULATIVE_METHOD_NONE)
        {
            std::wprintf(L"  This request decoded one token per step.\n");
        }
    }

    if (declaredCapacityDisclosure == WINML_SEQUENCE_CAPACITY_DISCLOSURE_ARTIFACT_DECLARED)
    {
        std::wprintf(L"Context capacity: effective=%llu declared=%llu\n",
                     static_cast<unsigned long long>(effectiveSequenceCapacity),
                     static_cast<unsigned long long>(declaredSequenceCapacity));
    }
    else
    {
        std::wprintf(L"Context capacity: effective=%llu declared=unknown\n",
                     static_cast<unsigned long long>(effectiveSequenceCapacity));
    }

    return 0;
}
catch (...)
{
    const HRESULT result = wil::ResultFromCaughtException();
    std::fprintf(stderr, "Text generation failed: 0x%08X\n", result);
    return 1;
}
