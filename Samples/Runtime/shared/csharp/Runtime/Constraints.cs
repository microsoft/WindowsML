// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Token constraints, which restrict the
// tokens a constrained generation may produce, such as to a JSON schema, and
// the output parser that turns generated text into conversation events. A
// formatted conversation describes both; chat completion streams expose the
// ones they use.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;

namespace WinMLSamples.Runtime;

public enum TokenConstraintStatus
{
    Active = 0,
    Completed = 1,
    Poisoned = 2,
}

[Flags]
public enum TokenConstraintStateFlags
{
    None = 0,
    AwaitingTrigger = 1,
    CanStop = 2,
}

public readonly record struct TokenConstraintState(
    TokenConstraintStatus Status,
    TokenConstraintStateFlags Flags,
    ulong AcceptedTokenCount);

// Creates token constraints from formatted conversations. Query a Tokenizer for it.
public sealed unsafe class TokenizerConstraintFactory : WinMLObject, IWinMLInterface<TokenizerConstraintFactory>
{
    private TokenizerConstraintFactory(IWinMLTokenizerConstraintFactory* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTokenizerConstraintFactory.IID_Guid;

    static TokenizerConstraintFactory IWinMLInterface<TokenizerConstraintFactory>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLTokenizerConstraintFactory*)pointer);

    // Creates the constraint that formatResult describes, such as the JSON
    // schema of its response format, in its initial state. formatResult must
    // come from this tokenizer. Throws NotSupportedException when it describes
    // no constraint.
    public TokenConstraint CreateConstraint(ConversationFormatResult formatResult)
    {
        ArgumentNullException.ThrowIfNull(formatResult);
        IWinMLTokenConstraint* constraint = null;
        Check(((IWinMLTokenizerConstraintFactory*)ThisPtr)->CreateConstraint(formatResult.Raw, &constraint));
        GC.KeepAlive(formatResult);
        return new TokenConstraint(Required(constraint));
    }
}

// The state of one constrained generation. Use it on the thread that created
// it, and pass it to one generation at a time.
public sealed unsafe class TokenConstraint : WinMLObject, IWinMLInterface<TokenConstraint>
{
    internal TokenConstraint(IWinMLTokenConstraint* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLTokenConstraint.IID_Guid;

    static TokenConstraint IWinMLInterface<TokenConstraint>.CreateFromOwnedPointer(nint pointer) => new((IWinMLTokenConstraint*)pointer);

    internal IWinMLTokenConstraint* Raw => (IWinMLTokenConstraint*)ThisPtr;

    public TokenConstraintState State
    {
        get
        {
            WINML_TOKEN_CONSTRAINT_STATE state;
            Check(Raw->GetState(&state));
            return new TokenConstraintState(
                (TokenConstraintStatus)state.status,
                (TokenConstraintStateFlags)state.stateFlags,
                state.acceptedTokenCount);
        }
    }

    // Advances the constraint with a token that it allows.
    public void AcceptToken(uint tokenId)
    {
        Check(Raw->AcceptToken(tokenId));
    }

    public bool IsStopToken(uint tokenId)
    {
        BOOL result;
        Check(Raw->IsStopToken(tokenId, &result));
        return result;
    }

    // Whether a stop token stays in the generated output.
    public bool IsPreservedToken(uint tokenId)
    {
        BOOL result;
        Check(Raw->IsPreservedToken(tokenId, &result));
        return result;
    }

    // Restores the initial state.
    public void Reset()
    {
        Check(Raw->Reset());
    }
}

// Turns a model's generated text into content, reasoning, and tool-call events.
public sealed unsafe class ConversationOutputParser : WinMLObject, IWinMLInterface<ConversationOutputParser>
{
    internal ConversationOutputParser(IWinMLConversationOutputParser* pointer)
        : base(pointer)
    {
    }

    public static Guid InterfaceId => IWinMLConversationOutputParser.IID_Guid;

    static ConversationOutputParser IWinMLInterface<ConversationOutputParser>.CreateFromOwnedPointer(nint pointer) =>
        new((IWinMLConversationOutputParser*)pointer);

    private IWinMLConversationOutputParser* Self => (IWinMLConversationOutputParser*)ThisPtr;

    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        fixed (char* value = text)
        {
            Check(Self->Append(new PCWSTR(value)));
        }
    }

    // Ends the text, so that the parser reports what it held back.
    public void Flush()
    {
        Check(Self->Flush());
    }

    // Returns false when the parser has no event ready. Dispose the event.
    public bool TryReadEvent([NotNullWhen(true)] out Tasks.ConversationOutputEvent? outputEvent)
    {
        WINML_CONVERSATION_OUTPUT_READ_STATUS status;
        IWinMLConversationOutputEvent* native = null;
        Check(Self->ReadEvent(&status, &native));
        if (status != WINML_CONVERSATION_OUTPUT_READ_STATUS.WINML_CONVERSATION_OUTPUT_READ_STATUS_EVENT)
        {
            Release(native);
            outputEvent = null;
            return false;
        }

        outputEvent = new Tasks.ConversationOutputEvent(Required(native));
        return true;
    }

    public void Reset()
    {
        Check(Self->Reset());
    }
}
