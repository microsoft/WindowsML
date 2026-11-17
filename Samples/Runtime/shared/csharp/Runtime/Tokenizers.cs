// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Tokenizers, streaming decoders, and chat
// template formatting.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;
using Native = Microsoft.Windows.AI.MachineLearning.Runtime.PInvoke;
using Win32 = Windows.Win32.PInvoke;

namespace WinMLSamples.Runtime;

public sealed unsafe class Tokenizer : WinMLObject, IWinMLInterface<Tokenizer>
{
    internal Tokenizer(IWinMLTokenizer* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTokenizer.IID_Guid;

    static Tokenizer IWinMLInterface<Tokenizer>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTokenizer*)pointer);

    internal IWinMLTokenizer* Raw => (IWinMLTokenizer*)ThisPtr;

    // Creates a tokenizer from a model directory with tokenizer.json, or from a GGUF file.
    public static Tokenizer CreateFromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IWinMLTokenizer* tokenizer = null;
        fixed (char* pathPointer = path)
        {
            WinMLException.ThrowIfFailed(Native.WinMLCreateTokenizerFromFile(new PCWSTR(pathPointer), &tokenizer));
        }

        return new Tokenizer(Required(tokenizer));
    }

    public uint[] Encode(string text, TokenizerEncodeFlags flags = TokenizerEncodeFlags.None)
    {
        ArgumentNullException.ThrowIfNull(text);
        uint count = 0;
        uint* tokens = null;
        fixed (char* textPointer = text)
        {
            Check(Raw->Encode(new PCWSTR(textPointer), (WINML_TOKENIZER_ENCODE_FLAGS)flags, &count, &tokens));
        }

        return TakeTokenIds(tokens, count);
    }

    // A decoder turns one token at a time into the text it adds.
    public TokenizerDecoder CreateDecoder()
    {
        IWinMLTokenizerDecoder* decoder = null;
        Check(Raw->CreateDecoder(&decoder));
        return new TokenizerDecoder(Required(decoder));
    }

    // Copies a token array that the Runtime allocated with CoTaskMemAlloc, and frees it.
    internal static uint[] TakeTokenIds(uint* tokens, uint count)
    {
        try
        {
            return tokens == null ? Array.Empty<uint>() : new ReadOnlySpan<uint>(tokens, checked((int)count)).ToArray();
        }
        finally
        {
            Win32.CoTaskMemFree(tokens);
        }
    }
}

public sealed unsafe class TokenizerDecoder : WinMLObject, IWinMLInterface<TokenizerDecoder>
{
    internal TokenizerDecoder(IWinMLTokenizerDecoder* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTokenizerDecoder.IID_Guid;

    static TokenizerDecoder IWinMLInterface<TokenizerDecoder>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTokenizerDecoder*)pointer);

    // Forgets earlier tokens, before decoding a new sequence.
    public void Reset()
    {
        Check(((IWinMLTokenizerDecoder*)ThisPtr)->Reset());
    }

    // The text that tokenId adds. Empty while a multi-token character is incomplete.
    public string DecodeToken(uint tokenId)
    {
        PCWSTR text;
        Check(((IWinMLTokenizerDecoder*)ThisPtr)->DecodeToken(tokenId, &text));
        string result = text.Value == null ? string.Empty : new string(text.Value);
        GC.KeepAlive(this);
        return result;
    }
}

// The tokenizer's special tokens. Query a Tokenizer for it with TryAs.
public sealed unsafe class TokenizerMetadata : WinMLObject, IWinMLInterface<TokenizerMetadata>
{
    private TokenizerMetadata(IWinMLTokenizerMetadata* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTokenizerMetadata.IID_Guid;

    static TokenizerMetadata IWinMLInterface<TokenizerMetadata>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTokenizerMetadata*)pointer);

    // Empty when the tokenizer declares no end-of-sequence token.
    public uint[] GetEosTokenIds()
    {
        uint count = 0;
        uint* tokens = null;
        Check(((IWinMLTokenizerMetadata*)ThisPtr)->GetEosTokenIds(&count, &tokens));
        return Tokenizer.TakeTokenIds(tokens, count);
    }

    // Returns false when the tokenizer declares no beginning-of-sequence token.
    public bool TryGetBosTokenId(out uint tokenId)
    {
        uint token = 0;
        HRESULT hr = ((IWinMLTokenizerMetadata*)ThisPtr)->GetBosTokenId(&token);
        Check(hr);
        tokenId = token;
        return hr.Value == 0;
    }
}

// Renders a conversation with the model's chat template. Query a Tokenizer for it.
public sealed unsafe class StructuredConversationFormatter : WinMLObject, IWinMLInterface<StructuredConversationFormatter>
{
    private StructuredConversationFormatter(IWinMLStructuredConversationFormatter* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLStructuredConversationFormatter.IID_Guid;

    static StructuredConversationFormatter IWinMLInterface<StructuredConversationFormatter>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLStructuredConversationFormatter*)pointer);

    public ConversationFormatResult FormatConversation(ConversationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int count = request.Messages.Count;
        WINML_CONVERSATION_MESSAGE[] messages = new WINML_CONVERSATION_MESSAGE[count];
        GCHandle[] pins = new GCHandle[count * 2];
        try
        {
            for (int i = 0; i < count; ++i)
            {
                pins[2 * i] = GCHandle.Alloc(request.Messages[i].Role, GCHandleType.Pinned);
                pins[(2 * i) + 1] = GCHandle.Alloc(request.Messages[i].Content, GCHandleType.Pinned);
                messages[i].role = new PCWSTR((char*)pins[2 * i].AddrOfPinnedObject());
                messages[i].content = new PCWSTR((char*)pins[(2 * i) + 1].AddrOfPinnedObject());
            }

            IWinMLConversationFormatResult* result = null;
            fixed (WINML_CONVERSATION_MESSAGE* messagePointer = messages)
            {
                WINML_CONVERSATION_REQUEST native = new()
                {
                    formatterMode = (WINML_CONVERSATION_FORMATTER_MODE)request.FormatterMode,
                    messageCount = (uint)count,
                    messages = messagePointer,
                    addGenerationPrompt = request.AddGenerationPrompt,
                };
                Check(((IWinMLStructuredConversationFormatter*)ThisPtr)->FormatConversation(&native, &result));
            }

            return new ConversationFormatResult(Required(result));
        }
        finally
        {
            foreach (GCHandle pin in pins)
            {
                if (pin.IsAllocated)
                {
                    pin.Free();
                }
            }
        }
    }
}

public sealed unsafe class ConversationFormatResult : WinMLObject, IWinMLInterface<ConversationFormatResult>
{
    internal ConversationFormatResult(IWinMLConversationFormatResult* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLConversationFormatResult.IID_Guid;

    static ConversationFormatResult IWinMLInterface<ConversationFormatResult>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLConversationFormatResult*)pointer);

    // The formatted prompt as token IDs.
    public uint[] GetTokenIds()
    {
        uint count = 0;
        uint* tokens = null;
        Check(((IWinMLConversationFormatResult*)ThisPtr)->GetTokenIds(&count, &tokens));
        return Tokenizer.TakeTokenIds(tokens, count);
    }
}
