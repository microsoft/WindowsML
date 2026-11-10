<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Capture Windows ML logs

These files record a Windows ML diagnostic trace and turn it into a readable
report, `WinmlRundown.log`. The report shows the Windows ML and ONNX Runtime
versions, which execution provider (EP) libraries loaded, which device each EP
ran on, whether a session fell back to the CPU, and any errors.

| File | Description |
| --- | --- |
| [`WinML.wprp`](WinML.wprp) | Windows Performance Recorder (WPR) profile that records Windows ML and ONNX Runtime events |
| [`Get-WinMLRundown.ps1`](Get-WinMLRundown.ps1) | Script that turns the trace into `WinmlRundown.log` |
| [`WindowsMLProfile.wpaProfile`](WindowsMLProfile.wpaProfile) | Windows Performance Analyzer (WPA) profile that the script uses to read the trace |

## Capture a trace

Run these commands in PowerShell as Administrator, from the folder that has
the three files.

```powershell
wpr -start .\WinML.wprp -filemode
# Reproduce the issue in your app
wpr -stop .\winml_trace.etl
.\Get-WinMLRundown.ps1 -EtlFilePath .\winml_trace.etl -WpaProfilePath .\WindowsMLProfile.wpaProfile -OutputFolder .\rundown_output
```

The report is written to `rundown_output\WinmlRundown.log`. The script needs
`wpaexporter.exe` from the Windows Performance Toolkit, and installs it if it
is missing.

## What the report shows

| Section | Shows |
| --- | --- |
| Onnx Version | ONNX Runtime version |
| WindowsAppSDK.ML Version | Windows ML version |
| WinML Runtime State, WinML Registered EP | Windows ML and ONNX Runtime versions, and the EPs Windows ML registered with their package and library path |
| EP Library Registration | Each EP library ONNX Runtime loaded, and whether it loaded |
| EP Auto Selection | Device policy, and the EPs it selected |
| Session Creation | Model and EPs for each session |
| EP Device Usage | Device (NPU, GPU, or CPU) and node count for each EP in a session |
| CPU Fallback | Whether nodes ran on the CPU: `NONE`, `PARTIAL`, `FULL`, `N/A`, or `UNKNOWN` |
| Driver Info | GPU driver |
| Errors | ONNX Runtime errors |

## Notes

- Start tracing before your app starts when you can. Most events are written
  once, when Windows ML and ONNX Runtime start up.
- If your app was already running when tracing started, newer Windows ML
  releases log their state (WinML Runtime State) when tracing starts and when
  the trace is saved.
- This profile doesn't record process names, so processes are listed as
  `Unknown (PID n)`.

For more detail, see [Capture Windows ML logs](https://learn.microsoft.com/windows/ai/new-windows-ml/logs)
on Microsoft Learn.
