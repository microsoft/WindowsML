<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Typed decisions

Evaluate a state against a choice question without generating text. The sample
loads a decision model folder and calls `CreateDecisionTaskFromModel` with a GPU
execution target, a null tokenizer and, when you pass `--program-cache`, model
options. With a null tokenizer the task creates the model's own tokenizer from
the folder. Python calls `tasks.create_decision_task(target, model)`; its
`tokenizer`, `compiled_program_directory` and `store_compiled_program_weights`
keywords are optional.

The sample prints each option's probability, the selected option, its
confidence, the recommendation and the number of input tokens. Capabilities
report whether the probabilities are calibrated. The result is immutable and
stays readable after the session closes.

```text
State: I was charged twice for my subscription.
Question: Which team should handle this support ticket?
billing: 0.9793
technical: 0.0207
Selected: billing; confidence: 0.9793; recommendation: act
```

Choice labels must be nonempty and unique. A recommendation is information for
the app, not permission to take an action.

```powershell
..\..\run_decision.ps1 -State "The app closes every time I open the settings page."
..\..\run_decision.ps1 -ProgramCache ..\..\models\program-cache
python.exe .\main.py ..\..\models\laya --question "How urgent is this request?" `
  --options low medium high
```

The first session compiles programs for the GPU. `--program-cache <directory>`
(run script `-ProgramCache`) keeps them for later runs, and `--cache-weights`
(`-CacheWeights`) also keeps the weights prepared for each program.

See [Models-backed Tasks](../README.md) for package and model requirements.
