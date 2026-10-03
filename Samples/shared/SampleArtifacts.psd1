# Copyright (C) Microsoft Corporation. All rights reserved.

# Single source of truth for every third-party artifact the samples need.
#
# Nothing here is downloaded automatically. check_artifacts.ps1 reports what is
# missing and prints the commands to acquire it, and the run scripts refuse
# to continue until the files are present. ProvenanceUrl is the publisher's page;
# the samples do not restate license terms, which readers verify at the source. Keep this file and
# docs\Runtime\artifacts.md in agreement; the documentation table is
# generated from these entries.
#
# Sha256 and SizeBytes identify the expected files. A file that does not match is
# reported as altered rather than silently reused.

@{
    SchemaVersion = 1

    Artifacts = @{
        'squeezenet-model' = @{
            DisplayName = 'SqueezeNet 1.1 image classifier (ONNX)'
            Purpose     = 'Classifies an image into ImageNet categories.'
            SourceUrl   = 'https://github.com/microsoft/WindowsAppSDK-Samples/raw/8adfdf35d8bb88114490ea3ba795b99bc52250a0/Samples/WindowsML/Resources/SqueezeNet.onnx'
            ProvenanceUrl = 'https://github.com/microsoft/WindowsAppSDK-Samples'
            SizeBytes   = 1307063
            Sha256      = 'd7f93e79ba1284a3ff2b4cea317d79f3e98e64acfce725ad5f4e8197864aef73'
            Path        = 'squeezenet\SqueezeNet.onnx'
        }
        'imagenet-labels' = @{
            DisplayName = 'ImageNet class labels'
            Purpose     = 'Maps classifier output indices to human-readable names.'
            SourceUrl   = 'https://raw.githubusercontent.com/pytorch/hub/a6fc887fbbbda0dd37c440bf8a145f1da6707d6b/imagenet_classes.txt'
            ProvenanceUrl = 'https://github.com/pytorch/hub'
            SizeBytes   = 10472
            Sha256      = '1f386e0d1cb6e28b9c2dac651c3dea6801e98ad1b41a14ce6bb1a093d72069f5'
            Path        = 'squeezenet\imagenet_classes.txt'
        }
        'squeezenet-sample-image' = @{
            DisplayName = 'Sample photograph'
            Purpose     = 'Provides a default input image for classification.'
            SourceUrl   = 'https://github.com/pytorch/hub/raw/a6fc887fbbbda0dd37c440bf8a145f1da6707d6b/images/dog.jpg'
            ProvenanceUrl = 'https://github.com/pytorch/hub'
            SizeBytes   = 661378
            Sha256      = 'f3f87bb8ab3c26c7ecfd3ac60421d7f32b0503d1d6c5baf8bac42ed93d86351a'
            Path        = 'squeezenet\sample-image.jpg'
        }

        'whisper-encoder' = @{
            DisplayName = 'Whisper medium encoder (ONNX, Q4F16)'
            Purpose     = 'Encodes audio features for speech recognition.'
            SourceUrl   = 'https://huggingface.co/onnx-community/whisper-medium-ONNX/resolve/d3978248a6b5de6df7ec29ddfbde3993845fa806/onnx/encoder_model_q4f16.onnx'
            ProvenanceUrl = 'https://huggingface.co/onnx-community/whisper-medium-ONNX'
            SizeBytes   = 180627406
            Sha256      = '6059784dd6670b28ccb67e0a35c66f4f3b936535f239946839b0615badc4b13f'
            Path        = 'whisper-medium-q4f16\source\encoder_model.onnx'
            TimeoutSeconds = 1800
        }
        'whisper-decoder' = @{
            DisplayName = 'Whisper medium decoder (ONNX, Q4F16)'
            Purpose     = 'Decodes encoded audio into transcript tokens.'
            SourceUrl   = 'https://huggingface.co/onnx-community/whisper-medium-ONNX/resolve/d3978248a6b5de6df7ec29ddfbde3993845fa806/onnx/decoder_model_q4f16.onnx'
            ProvenanceUrl = 'https://huggingface.co/onnx-community/whisper-medium-ONNX'
            SizeBytes   = 335345969
            Sha256      = 'db38a5c8370daabb1ba7cc254ae64c5b67c03002dccd8a019dfb2f770b037ebe'
            Path        = 'whisper-medium-q4f16\source\decoder_model.onnx'
            TimeoutSeconds = 1800
        }
        'whisper-vocab' = @{
            DisplayName = 'Whisper vocabulary'
            Purpose     = 'Maps decoder tokens back to text.'
            SourceUrl   = 'https://huggingface.co/onnx-community/whisper-medium-ONNX/resolve/d3978248a6b5de6df7ec29ddfbde3993845fa806/vocab.json'
            ProvenanceUrl = 'https://huggingface.co/onnx-community/whisper-medium-ONNX'
            SizeBytes   = 1036584
            Sha256      = '50d6a919f0a0601d56a04eb583c780d18553aa388254ba3158eb6a00f13e2c1a'
            Path        = 'whisper-medium-q4f16\onnx\vocab.json'
        }
        'whisper-tokenizer' = @{
            DisplayName = 'Whisper tokenizer'
            Purpose     = 'Supplies the Task API tokenizer definition.'
            SourceUrl   = 'https://huggingface.co/onnx-community/whisper-medium-ONNX/resolve/d3978248a6b5de6df7ec29ddfbde3993845fa806/tokenizer.json'
            ProvenanceUrl = 'https://huggingface.co/onnx-community/whisper-medium-ONNX'
            SizeBytes   = 3930494
            Sha256      = '7b469ff15eb7816315aa45eec391f5943d639b9d73d110f5c003df5192fd54e3'
            Path        = 'whisper-medium-q4f16\onnx\tokenizer.json'
        }
        'whisper-tokenizer-config' = @{
            DisplayName = 'Whisper tokenizer configuration'
            Purpose     = 'Supplies special tokens and decoding defaults.'
            SourceUrl   = 'https://huggingface.co/onnx-community/whisper-medium-ONNX/resolve/d3978248a6b5de6df7ec29ddfbde3993845fa806/tokenizer_config.json'
            ProvenanceUrl = 'https://huggingface.co/onnx-community/whisper-medium-ONNX'
            SizeBytes   = 282713
            Sha256      = '21a4fc0483c14b87f4e0bbc177a9a357479bfa7c95aaf21ad53d71e9c5afafb9'
            Path        = 'whisper-medium-q4f16\onnx\tokenizer_config.json'
        }

        'sesr-model' = @{
            DisplayName = 'SESR x2 super resolution (ONNX, FP32)'
            Purpose     = 'Upscales an image by two times.'
            SourceUrl   = 'https://huggingface.co/amd/ryzenai-sesr/resolve/bad8fad865bfb9cc88f72818c7079c6667de4d51/sesr_nchw_fp32.onnx'
            ProvenanceUrl = 'https://huggingface.co/amd/ryzenai-sesr'
            SizeBytes   = 93732
            Sha256      = '4b686864a8b17cf9aaad0d787f7b7a133c95317f408cac5204701d7291199711'
            Path        = 'sesr_x2\source\sesr_x2.onnx'
        }
        'sesr-sample-image' = @{
            DisplayName = 'Sample photograph'
            Purpose     = 'Provides a default low-resolution input image.'
            SourceUrl   = 'https://github.com/pytorch/hub/raw/a6fc887fbbbda0dd37c440bf8a145f1da6707d6b/images/dog.jpg'
            ProvenanceUrl = 'https://github.com/pytorch/hub'
            SizeBytes   = 661378
            Sha256      = 'f3f87bb8ab3c26c7ecfd3ac60421d7f32b0503d1d6c5baf8bac42ed93d86351a'
            Path        = 'sesr_x2\sample-image.jpg'
        }

        'qwen-gguf' = @{
            DisplayName = 'Qwen2.5 0.5B Instruct (GGUF, Q4_K_M)'
            Purpose     = 'Supplies a quantized language model for the GGUF backend.'
            SourceUrl   = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/9217f5db79a29953eb74d5343926648285ec7e67/qwen2.5-0.5b-instruct-q4_k_m.gguf'
            ProvenanceUrl = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF'
            SizeBytes   = 491400032
            Sha256      = '74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db'
            Path        = 'gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf'
            TimeoutSeconds = 1800
        }
    }

    # Artifacts produced on your machine rather than downloaded. Each command
    # runs locally and is described so you can see what it will do first.
    Preparations = @{
        'whisper-runtime' = @{
            DisplayName = 'Whisper Runtime-ready ONNX'
            Description = 'Rewrites the downloaded Whisper encoder and decoder into the fixed shapes the Runtime pipeline binds.'
            Produces    = @('whisper-medium-q4f16\onnx\encoder_model.onnx', 'whisper-medium-q4f16\onnx\decoder_model.onnx')
            Command     = '.\scripts\prepare_whisper_models.ps1 -SourceDirectory "{ModelsDirectory}\whisper-medium-q4f16\source" -OutputDirectory "{ModelsDirectory}\whisper-medium-q4f16\onnx"'
            Requires    = 'Python with the ONNX package'
            WorkingDirectory = 'Samples\Runtime'
        }
        'sesr-runtime' = @{
            DisplayName = 'SESR Runtime-ready ONNX'
            Description = 'Removes identity nodes the Runtime pipeline does not need.'
            Produces    = @('sesr_x2\sesr_x2.onnx')
            Command     = '.\scripts\prepare_sesr.ps1 -InputModel "{ModelsDirectory}\sesr_x2\source\sesr_x2.onnx" -OutputModel "{ModelsDirectory}\sesr_x2\sesr_x2.onnx"'
            Requires    = 'Python with the ONNX package'
            WorkingDirectory = 'Samples\Runtime'
        }
        'qwen-onnx' = @{
            DisplayName = 'Qwen2.5 0.5B Instruct exported to ONNX'
            Description = 'Downloads Qwen2.5-0.5B-Instruct from Hugging Face and exports the Runtime and Task ONNX graphs. Creates a local Python environment and installs the required export packages.'
            Produces    = @('llm\model.onnx', 'llm\task_model.onnx', 'llm\task_decode.onnx', 'llm\emb.onnx', 'llm\decoder.onnx', 'llm\head.onnx', 'llm\prefill.onnx', 'llm\tokenizer.json')
            Command     = '.\language\llm-chat\export_model.ps1 -OutputDir "{ModelsDirectory}\llm"'
            Requires    = 'About 7 GB of free disk space and internet access to huggingface.co'
            ProvenanceUrl = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct'
            WorkingDirectory = 'Samples\Runtime'
        }
    }

    # What each sample needs before it can run.
    Samples = @{
        'image-classification' = @{
            Title        = 'Image classification'
            Summary      = 'Classifies a photograph with SqueezeNet and prints the most likely ImageNet labels.'
            Artifacts    = @('squeezenet-model', 'imagenet-labels', 'squeezenet-sample-image')
            Preparations = @()
        }
        'model-compilation' = @{
            Title        = 'Model compilation'
            Summary      = 'Compiles SqueezeNet for the selected backend and runs the compiled artifact.'
            Artifacts    = @('squeezenet-model', 'imagenet-labels', 'squeezenet-sample-image')
            Preparations = @()
        }
        'super-resolution' = @{
            Title        = 'Super resolution'
            Summary      = 'Upscales an image two times with the SESR model.'
            Artifacts    = @('sesr-model', 'sesr-sample-image')
            Preparations = @('sesr-runtime')
        }
        'whisper' = @{
            Title        = 'Speech recognition'
            Summary      = 'Transcribes a WAV file with Whisper medium.'
            Artifacts    = @('whisper-encoder', 'whisper-decoder', 'whisper-vocab')
            Preparations = @('whisper-runtime')
        }
        'hello-language-model' = @{
            Title        = 'Hello language model'
            Summary      = 'Generates text from a prompt with the exported Qwen decoder.'
            Artifacts    = @()
            Preparations = @('qwen-onnx')
        }
        'llm-chat' = @{
            Title        = 'Language model chat'
            Summary      = 'Runs a multi-turn chat loop over the exported Qwen pipeline.'
            Artifacts    = @()
            Preparations = @('qwen-onnx')
        }
        'hello-language-model-gguf' = @{
            Title        = 'Hello language model, GGUF backend'
            Summary      = 'Generates text from a prompt with a quantized GGUF model.'
            Artifacts    = @('qwen-gguf')
            Preparations = @()
        }
        'llm-chat-gguf' = @{
            Title        = 'Language model chat, GGUF backend'
            Summary      = 'Runs the chat loop against a quantized GGUF model.'
            Artifacts    = @('qwen-gguf')
            Preparations = @()
        }
        'speech-to-language-model' = @{
            Title        = 'Speech to language model'
            Summary      = 'Transcribes audio and then answers a question about the transcript.'
            Artifacts    = @('whisper-encoder', 'whisper-decoder', 'whisper-vocab')
            Preparations = @('whisper-runtime', 'qwen-onnx')
        }
        'task-text-generation' = @{
            Title        = 'Text generation Task'
            Summary      = 'Generates text through the Task API with streaming, cancellation, and token counts.'
            Artifacts    = @()
            Preparations = @('qwen-onnx')
        }
        'task-text-generation-gguf' = @{
            Title        = 'Text generation Task, GGUF backend'
            Summary      = 'Generates text through the Task API using a quantized GGUF model.'
            Artifacts    = @('qwen-gguf')
            Preparations = @()
        }
        'task-automatic-speech-recognition' = @{
            Title        = 'Speech recognition Task'
            Summary      = 'Transcribes audio through the Task API.'
            Artifacts    = @('whisper-encoder', 'whisper-decoder', 'whisper-vocab', 'whisper-tokenizer', 'whisper-tokenizer-config')
            Preparations = @('whisper-runtime')
        }
        'task-speech-to-text-generation' = @{
            Title        = 'Speech to text generation composition'
            Summary      = 'Composes the speech recognition and text generation Tasks with the exported Qwen ONNX model.'
            Artifacts    = @('whisper-encoder', 'whisper-decoder', 'whisper-vocab', 'whisper-tokenizer', 'whisper-tokenizer-config')
            Preparations = @('whisper-runtime', 'qwen-onnx')
        }
        'task-speech-to-text-generation-gguf' = @{
            Title        = 'Speech to text generation composition, GGUF backend'
            Summary      = 'Composes the speech recognition and text generation Tasks with a quantized GGUF model.'
            Artifacts    = @('whisper-encoder', 'whisper-decoder', 'whisper-vocab', 'whisper-tokenizer', 'whisper-tokenizer-config', 'qwen-gguf')
            Preparations = @('whisper-runtime')
        }
        'server-in-process' = @{
            Title        = 'In-process server'
            Summary      = 'Hosts the OpenAI-compatible server inside an application and runs a client against it.'
            Artifacts    = @('qwen-gguf')
            Preparations = @()
        }
        'server-executable' = @{
            Title        = 'Server executable'
            Summary      = 'Serves a GGUF model with WinMLServer.exe for OpenAI-compatible clients.'
            Artifacts    = @('qwen-gguf')
            Preparations = @()
        }
    }

    # Execution choices a sample offers. Each option names the artifact set it
    # needs so a run script can report which choices are ready on this machine
    # instead of presenting one default as though it were the only option.
    Backends = @{
        'task-text-generation' = @{
            Default = 'ort'
            Options = @(
                @{
                    Id      = 'ort'
                    Title   = 'ONNX Runtime, CPU'
                    Summary = 'Runs the whole Task on CPU with the exported ONNX model.'
                    Sample  = 'task-text-generation'
                },
                @{
                    Id      = 'hybrid-ort'
                    Title   = 'Prefill and decode on ONNX Runtime, CPU'
                    Summary = 'Splits the Task into prefill and decode pipelines, both on CPU.'
                    Sample  = 'task-text-generation'
                },
                @{
                    Id      = 'llama'
                    Title   = 'GGUF, llama.cpp'
                    Summary = 'Runs a quantized GGUF model with the built-in tokenizer.'
                    Sample  = 'task-text-generation-gguf'
                }
            )
        }
        'task-chat-completion' = @{
            Default = 'ort'
            Options = @(
                @{
                    Id      = 'ort'
                    Title   = 'ONNX Runtime, CPU'
                    Summary = 'Holds the conversation on CPU with the exported ONNX model.'
                    Sample  = 'task-text-generation'
                },
                @{
                    Id      = 'llama'
                    Title   = 'GGUF, llama.cpp'
                    Summary = 'Holds the conversation with a quantized GGUF model.'
                    Sample  = 'task-text-generation-gguf'
                }
            )
        }
        'task-speech-to-text-generation-language' = @{
            Default = 'llama'
            Options = @(
                @{
                    Id      = 'llama'
                    Title   = 'GGUF, llama.cpp'
                    Summary = 'Answers using a quantized GGUF model.'
                    Sample  = 'task-speech-to-text-generation-gguf'
                },
                @{
                    Id      = 'ort'
                    Title   = 'ONNX Runtime'
                    Summary = 'Answers using the exported Qwen ONNX model.'
                    Sample  = 'task-speech-to-text-generation'
                }
            )
        }
        'hello-language-model' = @{
            Default = 'ort'
            Options = @(
                @{ Id = 'ort';   Title = 'ONNX Runtime'; Summary = 'Runs the exported Qwen decoder through ONNX Runtime.'; Sample = 'hello-language-model' },
                @{ Id = 'llama'; Title = 'GGUF, llama.cpp'; Summary = 'Runs a quantized GGUF model instead of the export.'; Sample = 'llm-chat-gguf' }
            )
        }
        'llm-chat' = @{
            Default = 'ort'
            Options = @(
                @{ Id = 'ort';   Title = 'ONNX Runtime'; Summary = 'Runs the exported Qwen pipeline through ONNX Runtime.'; Sample = 'llm-chat' },
                @{ Id = 'llama'; Title = 'GGUF, llama.cpp'; Summary = 'Runs a quantized GGUF model instead of the export.'; Sample = 'llm-chat-gguf' }
            )
        }
        'speech-to-language-model' = @{
            Default = 'ort'
            Options = @(
                @{ Id = 'ort';   Title = 'ONNX Runtime'; Summary = 'Transcribes and answers through ONNX Runtime.'; Sample = 'speech-to-language-model' },
                @{ Id = 'llama'; Title = 'GGUF, llama.cpp'; Summary = 'Answers with a quantized GGUF model after transcription.'; Sample = 'llm-chat-gguf' }
            )
        }
    }
}
