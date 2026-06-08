# Local Python wheels source

Drop `.whl` (or `.tar.gz` sdist) files in this folder to test pre-release or locally built Python packages without pushing them to a remote feed. pip picks them up automatically via the `--find-links ../../localpythonwheels` entry in each sample's `requirements.txt`.

Example:

```powershell
# Override windowsml with a local build
copy C:\path\to\windowsml-2.1.1-py3-none-any.whl .
```

Then run the install from the sample directory so the relative path resolves:

```powershell
cd Samples\python\SqueezeNetPython
pip install -r requirements.txt
```

To install **only** from this folder (ignore public PyPI), add `--no-index`:

```powershell
pip install -r requirements.txt --no-index
```

This folder is intentionally tracked, but its `.whl` / `.tar.gz` contents are ignored.
