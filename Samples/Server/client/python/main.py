# Copyright (C) Microsoft Corporation. All rights reserved.

r"""Python client: call the server's OpenAI-compatible endpoint with the OpenAI
Python library.

Connect to a running Windows ML Server, list its models, request a plain and
a streamed reply, and complete one tool call.

  The base URL and key come from WINMLSERVER_BASE_URL and
  WINMLSERVER_ACCESS_KEY, and the client ignores proxy settings from the
  environment. models.list() keeps the server's winml field, with each
  model's limits and tool support, in model_extra. chat.completions.create
  sends a plain request, a streamed request with include_usage, and, if
  tools are supported, a request with tool_choice "required"; the client
  runs multiply itself and returns the result with tool_choice "none".
  Every request asks for at most 256 tokens or the model's lower limit.

Run it
  python.exe -m pip install openai
  ..\..\run_in_process_server.ps1 -Client python
  ..\..\run_server_executable.ps1 -Client python

Learn more (paths relative to this file)
  ../README.md
  ../../../../docs/api-reference/IWinMLServer.md
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import dataclass

try:
    import openai
except ImportError as error:
    raise SystemExit(
        "This client needs the OpenAI Python library: python.exe -m pip install openai"
    ) from error

# The host sets these variables when it starts the client.
BASE_URL_VARIABLE = "WINMLSERVER_BASE_URL"
ACCESS_KEY_VARIABLE = "WINMLSERVER_ACCESS_KEY"

# Each request asks for at most this many tokens, or the model's own limit if
# that is lower. A request above the model's limit is rejected.
REQUESTED_OUTPUT_TOKENS = 256

# Bit 0 of capability_flags in the model list means the model supports tool
# calls.
TOOL_CALLS_CAPABILITY = 0x1

# The parameters are a JSON schema. When the model supports tool calls, the
# server makes its arguments match the schema.
MULTIPLY_TOOL = {
    "type": "function",
    "function": {
        "name": "multiply",
        "description": "Multiply two numbers.",
        "parameters": {
            "type": "object",
            "properties": {"a": {"type": "number"}, "b": {"type": "number"}},
            "required": ["a", "b"],
            "additionalProperties": False,
        },
    },
}


@dataclass(frozen=True)
class ModelInfo:
    id: str
    output_limit: int
    supports_tools: bool


def _list_models(client: openai.OpenAI, requested_id: str | None) -> ModelInfo:
    """Print the server's models and return the one this client uses."""
    models = client.models.list()
    print("Models")
    selected = None
    for model in models:
        # The winml field is specific to this server. The library keeps
        # fields it does not define in model_extra.
        details = (model.model_extra or {}).get("winml") or {}
        output_limit = int(details.get("maximum_output_tokens") or 0)
        supports_tools = bool(
            int(details.get("capability_flags") or 0) & TOOL_CALLS_CAPABILITY
        )
        print(
            f"  {model.id}: context window {details.get('context_window_tokens', 0)} tokens, "
            f"output limit {output_limit} tokens, tool calls "
            f"{'supported' if supports_tools else 'not supported'}"
        )
        if (selected is None and requested_id is None) or model.id == requested_id:
            selected = ModelInfo(model.id, output_limit, supports_tools)

    if selected is None:
        raise SystemExit(f"The server has no model named '{requested_id}'.")

    # A model that does not report its limit keeps the sample's own.
    output_limit = selected.output_limit
    if output_limit == 0 or output_limit > REQUESTED_OUTPUT_TOKENS:
        output_limit = REQUESTED_OUTPUT_TOKENS
    return ModelInfo(selected.id, output_limit, selected.supports_tools)


def _print_usage(usage) -> None:
    """Print token usage.

    prompt_tokens counts the whole formatted prompt. When the server can reuse
    the start of a prompt it already processed, cached_tokens reports how much
    of it was reused.
    """
    if usage is None:
        return
    reused = ""
    details = usage.prompt_tokens_details
    if details is not None and details.cached_tokens is not None:
        reused = f" ({details.cached_tokens} reused)"
    print(
        f"  Usage: {usage.prompt_tokens} prompt tokens{reused}, "
        f"{usage.completion_tokens} completion tokens"
    )


def _complete_chat(client: openai.OpenAI, model: ModelInfo) -> None:
    prompt = "Name three primary colors."
    print(f"\nChat completion\n  User: {prompt}")
    completion = client.chat.completions.create(
        model=model.id,
        messages=[
            {
                "role": "system",
                "content": "You are a helpful assistant. Answer briefly.",
            },
            {"role": "user", "content": prompt},
        ],
        max_completion_tokens=model.output_limit,
    )
    print(f"  Assistant: {completion.choices[0].message.content}")
    _print_usage(completion.usage)


def _stream_chat(client: openai.OpenAI, model: ModelInfo) -> None:
    """Print a reply as it arrives.

    Each chunk carries a delta to append. With include_usage set, a final
    chunk with no choices carries the usage.
    """
    prompt = "Count from one to five in words."
    print(f"\nStreaming\n  User: {prompt}\n  Assistant: ", end="", flush=True)
    stream = client.chat.completions.create(
        model=model.id,
        messages=[{"role": "user", "content": prompt}],
        max_completion_tokens=model.output_limit,
        stream=True,
        stream_options={"include_usage": True},
    )
    usage = None
    for chunk in stream:
        for choice in chunk.choices:
            if choice.delta.content:
                print(choice.delta.content, end="", flush=True)
        if chunk.usage is not None:
            usage = chunk.usage
    print()
    _print_usage(usage)


def _run_tool(name: str, arguments: str) -> str:
    """Run the function the model called.

    The model writes the arguments as JSON text, which the client parses
    before it runs the function.
    """
    if name != "multiply":
        return "error: unknown tool"
    try:
        values = json.loads(arguments)
        return json.dumps(values["a"] * values["b"])
    except (ValueError, KeyError, TypeError):
        return "error: invalid arguments"


def _call_tool(client: openai.OpenAI, model: ModelInfo) -> None:
    """Make one tool call and get the answer.

    The first request offers the tool and requires the model to call it. The
    client runs the call and adds the model's request and the result to the
    conversation. The second request returns the answer. An agent sends
    tool_choice "auto" instead and repeats until the model answers without
    calling a tool; this sample takes one call and then asks for the answer,
    so it always finishes in two requests.
    """
    prompt = "What is 12 times 34? Use the multiply tool."
    print(f"\nTool calling\n  User: {prompt}")
    messages = [{"role": "user", "content": prompt}]
    first = client.chat.completions.create(
        model=model.id,
        messages=messages,
        tools=[MULTIPLY_TOOL],
        tool_choice="required",
        max_completion_tokens=model.output_limit,
    )
    reply = first.choices[0].message
    messages.append(reply.to_dict())
    for call in reply.tool_calls or []:
        result = _run_tool(call.function.name, call.function.arguments)
        print(
            f"  Tool call: {call.function.name}({call.function.arguments}) returned {result}"
        )
        messages.append({"role": "tool", "tool_call_id": call.id, "content": result})

    second = client.chat.completions.create(
        model=model.id,
        messages=messages,
        tools=[MULTIPLY_TOOL],
        tool_choice="none",
        max_completion_tokens=model.output_limit,
    )
    print(f"  Assistant: {second.choices[0].message.content}")
    _print_usage(second.usage)


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Call the Windows ML Server's OpenAI-compatible endpoint."
    )
    parser.add_argument(
        "--model", help="Model id to use. Defaults to the first model the server lists."
    )
    return parser.parse_args()


def main() -> int:
    args = _parse_args()
    base_url = os.environ.get(BASE_URL_VARIABLE)
    access_key = os.environ.get(ACCESS_KEY_VARIABLE)
    if not base_url or not access_key:
        print(
            f"Set {BASE_URL_VARIABLE} and {ACCESS_KEY_VARIABLE}, "
            "or start this client from a run script.",
            file=sys.stderr,
        )
        return 2

    # The server listens only on this computer, so requests skip any proxy,
    # which would otherwise receive the access key.
    client = openai.OpenAI(
        base_url=base_url,
        api_key=access_key,
        http_client=openai.DefaultHttpxClient(trust_env=False),
    )
    print(f"Server: {base_url}\n")
    try:
        model = _list_models(client, args.model)
        _complete_chat(client, model)
        _stream_chat(client, model)
        if model.supports_tools:
            _call_tool(client, model)
        else:
            print(
                f"\nTool calling: skipped, because {model.id} does not support tool calls."
            )
    except openai.APIStatusError as error:
        # The library keeps the error object from the response in error.body.
        details = error.body if isinstance(error.body, dict) else {}
        message = details.get("message", error.message)
        print(
            f"The server returned HTTP {error.status_code}: {message}", file=sys.stderr
        )
        return 1
    except openai.APIConnectionError as error:
        print(f"Could not reach the server at {base_url}: {error}", file=sys.stderr)
        return 1
    except openai.APIError as error:
        # An error the server reports partway through a stream has no HTTP
        # status of its own.
        print(f"The server reported an error: {error.message}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
