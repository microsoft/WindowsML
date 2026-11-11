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

        'qwen-checkpoint-config' = @{
            DisplayName = 'Qwen2.5 0.5B Instruct model configuration'
            Purpose     = 'Describes the model architecture.'
            SourceUrl   = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/resolve/7ae557604adf67be50417f59c2c2f167def9a775/config.json'
            ProvenanceUrl = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct'
            SizeBytes   = 659
            Sha256      = '18e18afcaccafade98daf13a54092927904649e1dd4eba8299ab717d5d94ff45'
            Path        = 'qwen2.5-0.5b-instruct\config.json'
        }
        'qwen-checkpoint-weights' = @{
            DisplayName = 'Qwen2.5 0.5B Instruct weights (safetensors)'
            Purpose     = 'Supplies the model weights.'
            SourceUrl   = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/resolve/7ae557604adf67be50417f59c2c2f167def9a775/model.safetensors'
            ProvenanceUrl = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct'
            SizeBytes   = 988097824
            Sha256      = 'fdf756fa7fcbe7404d5c60e26bff1a0c8b8aa1f72ced49e7dd0210fe288fb7fe'
            Path        = 'qwen2.5-0.5b-instruct\model.safetensors'
            TimeoutSeconds = 1800
        }
        'qwen-checkpoint-tokenizer' = @{
            DisplayName = 'Qwen2.5 0.5B Instruct tokenizer'
            Purpose     = 'Converts between text and token IDs.'
            SourceUrl   = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/resolve/7ae557604adf67be50417f59c2c2f167def9a775/tokenizer.json'
            ProvenanceUrl = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct'
            SizeBytes   = 7031645
            Sha256      = 'c0382117ea329cdf097041132f6d735924b697924d6f6fc3945713e96ce87539'
            Path        = 'qwen2.5-0.5b-instruct\tokenizer.json'
        }
        'qwen-checkpoint-tokenizer-config' = @{
            DisplayName = 'Qwen2.5 0.5B Instruct tokenizer configuration'
            Purpose     = 'Supplies the special tokens and chat template.'
            SourceUrl   = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/resolve/7ae557604adf67be50417f59c2c2f167def9a775/tokenizer_config.json'
            ProvenanceUrl = 'https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct'
            SizeBytes   = 7305
            Sha256      = '5b5d4f65d0acd3b2d56a35b56d374a36cbc1c8fa5cf3b3febbbfabf22f359583'
            Path        = 'qwen2.5-0.5b-instruct\tokenizer_config.json'
        }
        'whisper-tiny-config' = @{
            DisplayName = 'Whisper tiny model configuration'
            Purpose     = 'Describes the model architecture.'
            SourceUrl   = 'https://huggingface.co/openai/whisper-tiny/resolve/169d4a4341b33bc18d8881c4b69c2e104e1cc0af/config.json'
            ProvenanceUrl = 'https://huggingface.co/openai/whisper-tiny'
            SizeBytes   = 1983
            Sha256      = 'ffdccec4f3211f4c63310f2b7098f309fe70f3952cedc5e4d11e43f5b2379b98'
            Path        = 'whisper-tiny\config.json'
        }
        'whisper-tiny-generation-config' = @{
            DisplayName = 'Whisper tiny generation configuration'
            Purpose     = 'Supplies the decoder start and language tokens.'
            SourceUrl   = 'https://huggingface.co/openai/whisper-tiny/resolve/169d4a4341b33bc18d8881c4b69c2e104e1cc0af/generation_config.json'
            ProvenanceUrl = 'https://huggingface.co/openai/whisper-tiny'
            SizeBytes   = 3747
            Sha256      = 'a5d5325911f16e74001a72fa13d6e208eee51548f994646de1f4b4cc8b35b512'
            Path        = 'whisper-tiny\generation_config.json'
        }
        'whisper-tiny-weights' = @{
            DisplayName = 'Whisper tiny weights (safetensors)'
            Purpose     = 'Supplies the model weights.'
            SourceUrl   = 'https://huggingface.co/openai/whisper-tiny/resolve/169d4a4341b33bc18d8881c4b69c2e104e1cc0af/model.safetensors'
            ProvenanceUrl = 'https://huggingface.co/openai/whisper-tiny'
            SizeBytes   = 151061672
            Sha256      = '7ebd0e69e78190ffe1438491fa05cc1f5c1aa3a4c4db3bc1723adbb551ea2395'
            Path        = 'whisper-tiny\model.safetensors'
            TimeoutSeconds = 1800
        }
        'whisper-tiny-tokenizer' = @{
            DisplayName = 'Whisper tiny tokenizer'
            Purpose     = 'Converts between text and token IDs.'
            SourceUrl   = 'https://huggingface.co/openai/whisper-tiny/resolve/169d4a4341b33bc18d8881c4b69c2e104e1cc0af/tokenizer.json'
            ProvenanceUrl = 'https://huggingface.co/openai/whisper-tiny'
            SizeBytes   = 2480466
            Sha256      = '27fc476bfe7f17299480be2273fc0608e4d5a99aba2ab5dec5374b4482d1a566'
            Path        = 'whisper-tiny\tokenizer.json'
        }
        'whisper-tiny-tokenizer-config' = @{
            DisplayName = 'Whisper tiny tokenizer configuration'
            Purpose     = 'Supplies the special tokens.'
            SourceUrl   = 'https://huggingface.co/openai/whisper-tiny/resolve/169d4a4341b33bc18d8881c4b69c2e104e1cc0af/tokenizer_config.json'
            ProvenanceUrl = 'https://huggingface.co/openai/whisper-tiny'
            SizeBytes   = 282683
            Sha256      = '2a4c4281cf9f51ac6ccc406fdc711a087afe6530f671fa7b80953edc498275ce'
            Path        = 'whisper-tiny\tokenizer_config.json'
        }
        'laya-config' = @{
            DisplayName = 'Laya model configuration'
            Purpose     = 'Describes the model architecture.'
            SourceUrl   = 'https://huggingface.co/convaiinnovations/laya/resolve/7b928d828b7b0e022f929d9bd2e44165aa270148/config.json'
            ProvenanceUrl = 'https://huggingface.co/convaiinnovations/laya'
            SizeBytes   = 160
            Sha256      = '7add7c041b53f7091e65908553af79ab3f077946e5f2312d510ae13b17c7c134'
            Path        = 'laya\config.json'
        }
        'laya-decision-config' = @{
            DisplayName = 'Laya decision head configuration'
            Purpose     = 'Supplies sequence budgets and calibration temperatures.'
            SourceUrl   = 'https://huggingface.co/convaiinnovations/laya/resolve/7b928d828b7b0e022f929d9bd2e44165aa270148/rl_agent_config.json'
            ProvenanceUrl = 'https://huggingface.co/convaiinnovations/laya'
            SizeBytes   = 745
            Sha256      = 'ae287b56bbcf5f8c4f4541ae9dfd00c914c4c48b940b8398c3058af37ba92bbd'
            Path        = 'laya\rl_agent_config.json'
        }
        'laya-encoder-config' = @{
            DisplayName = 'Laya encoder configuration'
            Purpose     = 'Describes the encoder architecture.'
            SourceUrl   = 'https://huggingface.co/convaiinnovations/laya/resolve/7b928d828b7b0e022f929d9bd2e44165aa270148/encoder/config.json'
            ProvenanceUrl = 'https://huggingface.co/convaiinnovations/laya'
            SizeBytes   = 2083
            Sha256      = 'bf3ab80598fdccf414855a2ce80f22859e4492d06ca8a62ddd1cfb63972f8979'
            Path        = 'laya\encoder\config.json'
        }
        'laya-weights' = @{
            DisplayName = 'Laya weights (safetensors)'
            Purpose     = 'Supplies the model weights.'
            SourceUrl   = 'https://huggingface.co/convaiinnovations/laya/resolve/7b928d828b7b0e022f929d9bd2e44165aa270148/model.safetensors'
            ProvenanceUrl = 'https://huggingface.co/convaiinnovations/laya'
            SizeBytes   = 842609210
            Sha256      = '891102d372688fc2a094dac56a384bc537b87c63f21f9f3dac0be2b7cbc8d86c'
            Path        = 'laya\model.safetensors'
            TimeoutSeconds = 1800
        }
        'laya-tokenizer' = @{
            DisplayName = 'Laya tokenizer'
            Purpose     = 'Converts text to token IDs.'
            SourceUrl   = 'https://huggingface.co/convaiinnovations/laya/resolve/7b928d828b7b0e022f929d9bd2e44165aa270148/tokenizer/tokenizer.json'
            ProvenanceUrl = 'https://huggingface.co/convaiinnovations/laya'
            SizeBytes   = 3583228
            Sha256      = '6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30'
            Path        = 'laya\tokenizer\tokenizer.json'
        }
        'laya-tokenizer-config' = @{
            DisplayName = 'Laya tokenizer configuration'
            Purpose     = 'Supplies the special tokens.'
            SourceUrl   = 'https://huggingface.co/convaiinnovations/laya/resolve/7b928d828b7b0e022f929d9bd2e44165aa270148/tokenizer/tokenizer_config.json'
            ProvenanceUrl = 'https://huggingface.co/convaiinnovations/laya'
            SizeBytes   = 308
            Sha256      = '50044de60daaa73df97d262e15a40d4faf0160e7d742df64b377877a1320dd12'
            Path        = 'laya\tokenizer\tokenizer_config.json'
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
        'task-model-text-generation' = @{
            Title        = 'Text generation from a checkpoint folder'
            Summary      = 'Streams a reply from published Qwen2.5 0.5B Instruct weights that Windows ML plans for the GPU.'
            Artifacts    = @('qwen-checkpoint-config', 'qwen-checkpoint-weights', 'qwen-checkpoint-tokenizer', 'qwen-checkpoint-tokenizer-config')
            Preparations = @()
        }
        'task-model-text-generation-gguf' = @{
            Title        = 'Text generation from a GGUF file'
            Summary      = 'Streams a reply from a Qwen2.5 0.5B Instruct GGUF file that Windows ML plans for the GPU.'
            Artifacts    = @('qwen-gguf')
            Preparations = @()
        }
        'task-composed-text-generation' = @{
            Title        = 'Text generation from a composition'
            Summary      = 'Describes the exported Qwen ONNX decoder as a composition and streams a reply.'
            Artifacts    = @()
            Preparations = @('qwen-onnx')
        }
        'task-llama-text-generation' = @{
            Title        = 'Text generation through llama.cpp'
            Summary      = 'Loads a GGUF file with the llama.cpp model loader and streams a reply.'
            Artifacts    = @('qwen-gguf')
            Preparations = @()
        }
        'task-model-speech-recognition' = @{
            Title        = 'Speech recognition from a checkpoint folder'
            Summary      = 'Transcribes a WAV file with published Whisper tiny weights.'
            Artifacts    = @('whisper-tiny-config', 'whisper-tiny-generation-config', 'whisper-tiny-weights', 'whisper-tiny-tokenizer', 'whisper-tiny-tokenizer-config')
            Preparations = @()
        }
        'task-decision' = @{
            Title        = 'Typed decisions'
            Summary      = 'Answers a choice question with the published Laya decision model.'
            Artifacts    = @('laya-config', 'laya-decision-config', 'laya-encoder-config', 'laya-weights', 'laya-tokenizer', 'laya-tokenizer-config')
            Preparations = @()
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
