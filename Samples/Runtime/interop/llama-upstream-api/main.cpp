// Copyright (C) Microsoft Corporation. All rights reserved.
//
// llama.cpp upstream API: generate text with llama.h against the llama.cpp
// build that Windows ML ships.
//
// The Microsoft.Windows.AI.MachineLearning.LibLlama.Core package deploys the
// llama.cpp core beside this executable. Because the project sets
// WinMLLinkLlamaUpstreamApi, the package also supplies llama.cpp's own headers
// and the import libraries of that core, so this file calls the llama.cpp API
// directly and uses no Windows ML API.
//
//   ggml_backend_load_all_from_path loads the ggml backends that the build
//   deployed under LlamaBackends, one folder per backend family. The sample
//   then loads a GGUF model, formats the prompt with the model's chat
//   template, tokenizes it, and runs llama_decode in a loop. Each step takes
//   the most likely next token (greedy sampling) and prints it, until the
//   model ends its turn or --max-tokens tokens are generated.
//
// Run it
//   llama-upstream-api.exe --model <path> [--prompt <text>] [--max-tokens <count>]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Runtime/gguf-models.md

#include <windows.h>

#include <ggml-backend.h>
#include <llama-cpp.h>
#include <llama.h>

#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <exception>
#include <filesystem>
#include <string>
#include <string_view>
#include <system_error>
#include <vector>

namespace
{

struct Options
{
    std::string modelPath;
    std::string prompt = "Explain in one sentence why the sky appears blue.";
    int maxTokens = 64;
};

void PrintUsage()
{
    std::fputs("Usage: llama-upstream-api.exe --model PATH [--prompt TEXT] [--max-tokens COUNT]\n"
               "  --model PATH        GGUF model file.\n"
               "  --prompt TEXT       Prompt text.\n"
               "  --max-tokens COUNT  Maximum number of generated tokens (default 64).\n",
               stderr);
}

// The llama.cpp API takes UTF-8 strings, including file paths.
std::string ToUtf8(std::wstring_view text)
{
    if (text.empty())
    {
        return {};
    }

    const int textLength = static_cast<int>(text.size());
    const int size =
        WideCharToMultiByte(CP_UTF8, 0, text.data(), textLength, nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), textLength, result.data(), size, nullptr, nullptr);
    return result;
}

bool ParseOptions(int argc, wchar_t** argv, Options& options)
{
    if (argc % 2 == 0)
    {
        return false;
    }

    for (int index = 1; index < argc; index += 2)
    {
        const std::wstring_view name = argv[index];
        const wchar_t* value = argv[index + 1];
        if (name == L"--model")
        {
            options.modelPath = ToUtf8(value);
        }
        else if (name == L"--prompt")
        {
            options.prompt = ToUtf8(value);
        }
        else if (name == L"--max-tokens")
        {
            wchar_t* end = nullptr;
            errno = 0;
            const long count = std::wcstol(value, &end, 10);
            if (errno != 0 || *end != L'\0' || count <= 0)
            {
                return false;
            }

            options.maxTokens = static_cast<int>(count);
        }
        else
        {
            return false;
        }
    }

    return !options.modelPath.empty() && !options.prompt.empty();
}

std::filesystem::path ExecutableDirectory()
{
    std::wstring path(MAX_PATH, L'\0');
    DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    while (length == path.size())
    {
        path.resize(path.size() * 2);
        length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    }

    path.resize(length);
    return std::filesystem::path(path).parent_path();
}

// Prints llama.cpp errors and drops its informational output and warnings.
void LogErrors(ggml_log_level level, const char* text, void*)
{
    // GGML_LOG_LEVEL_CONT continues the previous message at that message's level.
    static ggml_log_level messageLevel = GGML_LOG_LEVEL_NONE;
    if (level != GGML_LOG_LEVEL_CONT)
    {
        messageLevel = level;
    }

    if (messageLevel == GGML_LOG_LEVEL_ERROR)
    {
        std::fputs(text, stderr);
    }
}

// Formats the prompt as one user message with the model's chat template.
// llama_chat_apply_template recognizes a fixed set of common templates and does
// not run Jinja, so a model without a recognized template gets the prompt as is.
std::string ApplyChatTemplate(const llama_model* model, const std::string& prompt)
{
    const char* chatTemplate = llama_model_chat_template(model, nullptr);
    if (chatTemplate == nullptr)
    {
        return prompt;
    }

    const llama_chat_message message{"user", prompt.c_str()};
    std::vector<char> buffer(2 * prompt.size() + 256);
    int32_t length = llama_chat_apply_template(chatTemplate, &message, 1, true, buffer.data(),
                                               static_cast<int32_t>(buffer.size()));
    if (length > static_cast<int32_t>(buffer.size()))
    {
        buffer.resize(static_cast<size_t>(length));
        length = llama_chat_apply_template(chatTemplate, &message, 1, true, buffer.data(), length);
    }

    return length > 0 ? std::string(buffer.data(), static_cast<size_t>(length)) : prompt;
}

std::vector<llama_token> Tokenize(const llama_vocab* vocab, const std::string& text)
{
    // add_special adds a BOS token when the model expects one, and parse_special
    // turns the chat template's control text into control tokens. Without an
    // output buffer, llama_tokenize returns the negated token count.
    const int32_t textLength = static_cast<int32_t>(text.size());
    const int32_t required =
        llama_tokenize(vocab, text.c_str(), textLength, nullptr, 0, true, true);
    if (required >= 0 || required == INT32_MIN)
    {
        return {};
    }

    std::vector<llama_token> tokens(static_cast<size_t>(-required));
    if (llama_tokenize(vocab, text.c_str(), textLength, tokens.data(), -required, true, true) !=
        -required)
    {
        return {};
    }

    return tokens;
}

int Run(const Options& options)
{
    // The build deploys each backend family in its own folder, for example
    // LlamaBackends\cpu\ggml-cpu.dll, and loading a backend registers its devices.
    // While ggml loads a folder's backend, the folder is on the DLL search path, so
    // libraries placed beside the backend, such as the NVIDIA libraries that a CUDA
    // backend imports, resolve.
    const std::filesystem::path backendRoot = ExecutableDirectory() / L"LlamaBackends";
    std::error_code error;
    for (const auto& entry : std::filesystem::directory_iterator(backendRoot, error))
    {
        if (entry.is_directory(error))
        {
            SetDllDirectoryW(entry.path().c_str());
            ggml_backend_load_all_from_path(ToUtf8(entry.path().native()).c_str());
            SetDllDirectoryW(nullptr);
        }
    }

    if (ggml_backend_dev_count() == 0)
    {
        std::fprintf(stderr, "No ggml backend was loaded from %s.\n",
                     ToUtf8(backendRoot.native()).c_str());
        return 1;
    }

    for (size_t index = 0; index < ggml_backend_dev_count(); ++index)
    {
        ggml_backend_dev_t device = ggml_backend_dev_get(index);
        std::printf("Device: %s (%s)\n", ggml_backend_dev_name(device),
                    ggml_backend_dev_description(device));
    }

    // The default parameters place every layer on a GPU when a GPU backend is loaded.
    llama_model_ptr model(
        llama_model_load_from_file(options.modelPath.c_str(), llama_model_default_params()));
    if (!model)
    {
        std::fprintf(stderr, "Could not load the model %s.\n", options.modelPath.c_str());
        return 1;
    }

    const llama_vocab* vocab = llama_model_get_vocab(model.get());
    std::vector<llama_token> tokens =
        Tokenize(vocab, ApplyChatTemplate(model.get(), options.prompt));
    if (tokens.empty())
    {
        std::fputs("Could not tokenize the prompt.\n", stderr);
        return 1;
    }

    // The context holds the prompt and the generated tokens, and n_batch lets one
    // llama_decode call process the whole prompt.
    llama_context_params contextParams = llama_context_default_params();
    contextParams.n_ctx =
        static_cast<uint32_t>(tokens.size()) + static_cast<uint32_t>(options.maxTokens);
    contextParams.n_batch = static_cast<uint32_t>(tokens.size());
    llama_context_ptr context(llama_init_from_model(model.get(), contextParams));
    if (!context)
    {
        std::fputs("Could not create the llama.cpp context.\n", stderr);
        return 1;
    }

    llama_sampler_ptr sampler(llama_sampler_chain_init(llama_sampler_chain_default_params()));
    llama_sampler_chain_add(sampler.get(), llama_sampler_init_greedy());

    std::printf("Prompt: %s\nResponse: ", options.prompt.c_str());
    llama_batch batch = llama_batch_get_one(tokens.data(), static_cast<int32_t>(tokens.size()));
    llama_token token = LLAMA_TOKEN_NULL;
    std::vector<char> piece(256);
    for (int generated = 0; generated < options.maxTokens; ++generated)
    {
        const int32_t status = llama_decode(context.get(), batch);
        if (status != 0)
        {
            std::fprintf(stderr, "\nllama_decode failed with status %d.\n", status);
            return 1;
        }

        token = llama_sampler_sample(sampler.get(), context.get(), -1);
        if (llama_vocab_is_eog(vocab, token))
        {
            break;
        }

        // A piece longer than the buffer returns its negated length, so grow the
        // buffer to that length and convert the token again.
        int32_t length = llama_token_to_piece(vocab, token, piece.data(),
                                              static_cast<int32_t>(piece.size()), 0, false);
        if (length < 0)
        {
            piece.resize(static_cast<size_t>(-length));
            length = llama_token_to_piece(vocab, token, piece.data(),
                                          static_cast<int32_t>(piece.size()), 0, false);
        }

        if (length < 0)
        {
            std::fputs("\nCould not convert a token to text.\n", stderr);
            return 1;
        }

        std::fwrite(piece.data(), 1, static_cast<size_t>(length), stdout);
        std::fflush(stdout);
        batch = llama_batch_get_one(&token, 1);
    }

    std::fputs("\n", stdout);
    return 0;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 &&
        (std::wstring_view(argv[1]) == L"--help" || std::wstring_view(argv[1]) == L"-h"))
    {
        PrintUsage();
        return 0;
    }

    Options options;
    if (!ParseOptions(argc, argv, options))
    {
        PrintUsage();
        return 2;
    }

    // llama.cpp produces UTF-8 text.
    const UINT previousCodePage = GetConsoleOutputCP();
    SetConsoleOutputCP(CP_UTF8);

    llama_log_set(LogErrors, nullptr);
    llama_backend_init();

    int exitCode = 1;
    try
    {
        exitCode = Run(options);
    }
    catch (const std::exception& exception)
    {
        std::fprintf(stderr, "%s\n", exception.what());
    }

    llama_backend_free();
    if (previousCodePage != 0)
    {
        SetConsoleOutputCP(previousCodePage);
    }

    return exitCode;
}
