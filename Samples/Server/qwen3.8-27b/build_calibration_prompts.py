# Copyright (C) Microsoft Corporation. All rights reserved.

"""Build KV calibration prompts for Qwen3.8-27B from public sources.

The prompts cover the content a coding model serves, because each kind of text
reaches attention channels the others don't: long repository contexts in
Python, C and C++, model-card prose, a multi-turn coding chat, an agent
transcript with tool calls, step-by-step reasoning, and documentation in nine
languages.

Every input is a public file pinned by URL and SHA-256 in
calibration_sources.json, which also records each source's license. The script
downloads each file once, one at a time, into a cache folder, verifies its
hash, and stops if a file differs. It writes JSON Lines of
{"id": ..., "prompt": ...}, rendered with Qwen3.8's chat template, for
calibrate_kv_scales.py. The cache and the output default to the gitignored
Samples\\Server\\models folder.

Usage:
  python build_calibration_prompts.py --tokenizer <model folder>\\tokenizer.json
  python build_calibration_prompts.py --tokenizer <model folder>\\tokenizer.json --output prompts.jsonl
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import re
import sys
import urllib.request
from pathlib import Path

from tokenizers import Tokenizer

HERE = Path(__file__).resolve().parent
MODELS = HERE.parent / "models"


class Sources:
    """Downloads and verifies the pinned files."""

    def __init__(self, manifest: Path, cache: Path):
        self.sources = json.loads(manifest.read_text(encoding="utf-8"))["sources"]
        self.cache = cache

    def files(self, source: str) -> list[dict]:
        return self.sources[source]["files"]

    def read(self, entry: dict) -> bytes:
        name = hashlib.sha256(entry["url"].encode("utf-8")).hexdigest()[:16] + "-" + Path(entry["url"]).name
        path = self.cache / name
        if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
            self.cache.mkdir(parents=True, exist_ok=True)
            print(f"downloading {entry['url']}", flush=True)
            with urllib.request.urlopen(entry["url"], timeout=300) as response:
                data = response.read()
            digest = hashlib.sha256(data).hexdigest()
            if digest != entry["sha256"]:
                raise SystemExit(f"{entry['url']} has SHA-256 {digest}; expected {entry['sha256']}")
            partial = path.with_suffix(path.suffix + ".part")
            partial.write_bytes(data)
            partial.replace(path)
        return path.read_bytes()

    def text(self, entry: dict) -> str:
        return self.read(entry).decode("utf-8", errors="replace").replace("\r\n", "\n")


def chat(turns: list[tuple[str, str]], think: bool = False) -> str:
    rendered = "".join(f"<|im_start|>{role}\n{text}<|im_end|>\n" for role, text in turns)
    return rendered + "<|im_start|>assistant\n" + ("<think>\n" if think else "<think>\n\n</think>\n\n")


def repository_context(sources: Sources, tokenizer: Tokenizer, source: str, budget: int) -> str:
    """Concatenates a repository's files, in manifest order, up to a token budget."""
    parts, used = [], 0
    for entry in sources.files(source):
        block = f"[start of {entry['path']}]\n{sources.text(entry)}\n[end of {entry['path']}]\n"
        ids = tokenizer.encode(block, add_special_tokens=False).ids
        if used + len(ids) > budget:
            parts.append(tokenizer.decode(ids[: budget - used]))
            break
        parts.append(block)
        used += len(ids)
    return "".join(parts)


def build(sources: Sources, tokenizer: Tokenizer) -> list[tuple[str, str]]:
    prompts = []
    review = "\n\nReview the code above. Describe its structure, then point out the riskiest function and why."
    for name, source, budget in (("sympy-108k", "sympy", 108_000), ("redis-32k", "redis", 32_000),
                                 ("fmt-32k", "fmt", 32_000), ("windowsml-cpp", "windowsml", 24_000)):
        prompts.append((name, chat([("user", repository_context(sources, tokenizer, source, budget) + review)])))

    cards = "".join(sources.text(entry) + "\n\n" for entry in sources.files("model_cards"))
    prompts.append(("prose-cards", chat([("user", cards[:70_000] + "\n\nSummarize the documents above.")])))

    # A multi-turn coding chat: HumanEval+ tasks, each answered briefly.
    turns, size = [], 0
    data = gzip.decompress(sources.read(sources.files("humaneval_plus")[0])).decode("utf-8")
    for line in data.splitlines():
        task = json.loads(line)["prompt"]
        turns += [("user", "Complete this function:\n```python\n" + task + "```"),
                  ("assistant", "Here is the function:\n```python\n# implementation omitted\n```")]
        size += len(task)
        if size > 40_000:
            break
    prompts.append(("chat-humaneval", chat(turns)))

    # An agent transcript: a read_file call and its result per fmt source file, then a test run.
    tools = [{"type": "function", "function": {"name": "read_file", "description": "Read a file from the repository.",
                                               "parameters": {"type": "object", "properties": {"path": {"type": "string"}},
                                                              "required": ["path"]}}},
             {"type": "function", "function": {"name": "run_tests", "description": "Run the unit tests.",
                                               "parameters": {"type": "object",
                                                              "properties": {"filter": {"type": "string"}}}}}]
    system = ("# Tools\n\nYou may call one or more functions to assist with the user query.\n\n<tools>\n"
              + "\n".join(json.dumps(t) for t in tools)
              + "\n</tools>\n\nFor each function call, return a json object with function name and arguments within "
              "<tool_call></tool_call> XML tags.")
    turns = [("system", system), ("user", "The range formatter in fmt prints nested ranges incorrectly. "
                                          "Please investigate and fix the issue.")]
    for entry in sources.files("fmt"):
        turns += [("assistant", f"I will read `{entry['path']}`.\n<tool_call>\n"
                   + json.dumps({"name": "read_file", "arguments": {"path": entry["path"]}}) + "\n</tool_call>"),
                  ("user", "<tool_response>\n" + sources.text(entry)[:12_000] + "\n</tool_response>")]
    turns += [("assistant", "<tool_call>\n" + json.dumps({"name": "run_tests", "arguments": {"filter": "ranges-test"}})
               + "\n</tool_call>"),
              ("user", "<tool_response>\n[==========] 112 tests from 9 test suites ran. (431 ms total)\n"
                       "[  PASSED  ] 111 tests.\n[  FAILED  ] 1 test, listed below:\n[  FAILED  ] ranges_test.nested\n"
                       "</tool_response>")]
    prompts.append(("agent-toolcalls", chat(turns)))

    # Step-by-step reasoning in <think> blocks: GSM8K problems and their worked solutions.
    turns, size = [], 0
    for line in sources.text(sources.files("gsm8k")[0]).splitlines():
        item = json.loads(line)
        steps, _, answer = item["answer"].partition("####")
        steps = re.sub(r"<<[^>]*>>", "", steps).strip()
        turns += [("user", item["question"]),
                  ("assistant", f"<think>\n{steps}\n</think>\n\nThe answer is {answer.strip()}.")]
        size += len(item["question"]) + len(steps)
        if size > 90_000:
            break
    prompts.append(("reasoning-gsm8k", chat(turns, think=True)))

    docs = "\n\n".join(f"[{entry['language']}]\n{sources.text(entry)}" for entry in sources.files("multilingual"))
    prompts.append(("multilingual", chat([("system", "You are a helpful multilingual assistant."),
                                          ("user", docs + "\n\nTranslate each document above into English.")])))
    return prompts


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--tokenizer", type=Path, required=True, help="tokenizer.json from the model folder")
    parser.add_argument("--sources", type=Path, default=HERE / "calibration_sources.json")
    parser.add_argument("--cache", type=Path, default=MODELS / "calibration-cache")
    parser.add_argument("--output", type=Path, default=MODELS / "qwen3.8-27b-calibration-prompts.jsonl")
    args = parser.parse_args()

    tokenizer = Tokenizer.from_file(str(args.tokenizer))
    prompts = build(Sources(args.sources, args.cache), tokenizer)
    with args.output.open("w", encoding="utf-8", newline="\n") as out:
        for name, prompt in prompts:
            out.write(json.dumps({"id": name, "prompt": prompt}, ensure_ascii=False) + "\n")
    total = 0
    for name, prompt in prompts:
        tokens = len(tokenizer.encode(prompt, add_special_tokens=False).ids)
        total += tokens
        print(f"{name:<18} {tokens:>8,} tokens")
    digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
    print(f"{args.output}: {len(prompts)} prompts, {total:,} tokens, SHA-256 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
