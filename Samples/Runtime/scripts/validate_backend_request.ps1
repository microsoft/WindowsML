# Copyright (C) Microsoft Corporation. All rights reserved.

function Assert-SampleBackendRequest {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("ort", "llama")]
        [string]$Backend,

        [Parameter(Mandatory)]
        [ValidateSet("cpu", "gpu", "npu")]
        [string]$Device,

        [string]$Ep,

        # Set when the sample uses the bundled language model export.
        [switch]$BundledLanguageModel
    )

    if ($Backend -eq "ort" -and $Device -eq "npu" -and $BundledLanguageModel) {
        throw "The bundled language model export is built for CPU and GPU. To try an NPU, pass -ModelPath with a model prepared for that NPU."
    }

    if ($Backend -eq "llama") {
        if ($Device -eq "npu") {
            throw "-Backend llama supports CPU or GPU, not NPU."
        }
        if ($Ep) {
            throw "-Ep applies to ORT and cannot be combined with -Backend llama."
        }
    }
}
