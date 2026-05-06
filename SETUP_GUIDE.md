# MedVid AR Assistant — Complete Installation Guide

## Prerequisites

- Windows 10/11 PC with NVIDIA RTX 4090 (or any GPU with 24GB+ VRAM)
- Meta Quest 3 headset with developer mode enabled
- USB-C cable (must support data transfer, not charge-only)
- ~80GB free disk space (models + videos)

---

## Final Folder Structure

This is what the LOGS folder should look like when done:

```
C:\Users\YOUR_USERNAME\Documents\LOGS\
├── logger_server.py
├── search_engine.py
├── download_videos.py
├── Kokoro/
│   ├── kokoro-v0_19.onnx         (310 MB)
│   └── voices.json               (27 MB - renamed from voices.bin)
├── MedVid_DATA/
│   ├── medical_db.json
│   ├── transcripts.json
│   └── videos/                   (744 .mp4 files)
├── mission_log.xlsx              (auto-created on first run)
└── Mission_Logs/                 (auto-created on first run)
```

---

## Step 1: Install WSL2

> **Open: Windows PowerShell (Run as Administrator)**

```powershell
wsl --install
```

Restart PC. Ubuntu opens and asks for username/password.

Verify:

```powershell
wsl --list --verbose
```

---

## Step 2: Install System Packages

> **Open: Ubuntu terminal**

```bash
sudo apt install -y python3 python3-pip python3-venv build-essential libffi-dev libssl-dev
sudo apt install -y ffmpeg espeak-ng espeak-ng-data git
```

---

## Step 3: Install Python Packages

> **Open: Ubuntu terminal**

```bash
pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu124 --break-system-packages
pip install vllm --break-system-packages
pip install faster-whisper --break-system-packages
pip install kokoro-onnx --break-system-packages
pip install fastapi uvicorn python-multipart openai soundfile openpyxl numpy --break-system-packages
pip install yt-dlp --break-system-packages
```

---

## Step 4: Set Up Project Folder

> **Open: Ubuntu terminal**

```bash
cd /mnt/c/Users/YOUR_USERNAME/Documents
mkdir -p LOGS
cd LOGS
```

---

## Step 5: Copy Project Files

Copy into the LOGS folder:

- `logger_server.py`
- `search_engine.py`
- `download_videos.py`

---

## Step 6: Update Paths in Code

> **Open: Ubuntu terminal** (inside LOGS)

```bash
sed -i 's/tbahaaal/YOUR_USERNAME/g' logger_server.py
sed -i 's/tbahaaal/YOUR_USERNAME/g' search_engine.py
```

---

## Step 7: Download Kokoro TTS Model

> **Open: Ubuntu terminal** (inside LOGS)

```bash
mkdir -p Kokoro
```

You need two files inside the `Kokoro/` folder:

1. **kokoro-v0_19.onnx** (~310 MB) — the TTS model
2. **voices.json** (~27 MB) — the voice configuration

Download them from https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files (under "Assets"). If the link doesn't work, search GitHub for "kokoro-onnx releases model-files" and download from there.

**Important:** The voices file may be named `voices.bin` on the download page. Rename it to `voices.json`:

```bash
cp /mnt/c/Users/YOUR_USERNAME/Downloads/kokoro-v0_19.onnx Kokoro/
cp /mnt/c/Users/YOUR_USERNAME/Downloads/voices.bin Kokoro/voices.json
```

Verify both files are there:

```bash
ls -la Kokoro/
```

---

## Step 8: Set Up Medical Data

Copy `medical_db.json` and `transcripts.json` into `MedVid_DATA/`:

```bash
mkdir -p MedVid_DATA/videos
```

Place files so you have:
- `MedVid_DATA/medical_db.json`
- `MedVid_DATA/transcripts.json`

---

## Step 9: Download Medical Videos

> **Open: Ubuntu terminal** (inside LOGS)

```bash
python3 download_videos.py
```

Downloads 744 videos from YouTube. Takes a long time. Can be stopped and resumed (skips existing files).

---

## Step 10: Install ADB

> **Open: Windows PowerShell**

```powershell
winget install Google.PlatformTools
```

Close and reopen Command Prompt. Verify:

```cmd
adb version
```

If `winget` doesn't work: download from https://developer.android.com/tools/releases/platform-tools, extract to `C:\platform-tools`, add to PATH:

```powershell
[Environment]::SetEnvironmentVariable("Path", $env:Path + ";C:\platform-tools", "Machine")
```

---

## Step 11: Enable Quest 3 Developer Mode

1. On phone → **Meta Horizon** app → Menu → Devices → Quest 3
2. Settings → Developer Mode → **ON**
3. Restart Quest 3

---

## Step 12: Connect Quest 3

Plug Quest 3 into PC. Put on headset, tap **Always Allow** for USB debugging.

When Windows Firewall asks about ADB → click **Allow**.

> **Open: Windows Command Prompt**

```cmd
adb kill-server
adb start-server
adb devices
```

Should show device ID with `device` (not `unauthorized`).

---

## Step 13: Copy Videos to Quest

> **Open: Windows Command Prompt**

```cmd
adb shell mkdir -p /sdcard/Movies/MedicalVideos
adb push "C:\Users\YOUR_USERNAME\Documents\LOGS\MedVid_DATA\videos\." /sdcard/Movies/MedicalVideos/
```

---

## Step 14: Set Up Port Forwarding

### ADB Reverse Tunnel

> **Open: Windows Command Prompt**

```cmd
adb reverse tcp:8000 tcp:8000
```

### Windows → WSL2 Port Proxy

> **Open: Windows PowerShell (Run as Administrator)**

```powershell
wsl hostname -I
```

Note the IP (e.g., `172.20.210.22`), then:

```powershell
netsh interface portproxy add v4tov4 listenport=8000 listenaddress=0.0.0.0 connectport=8000 connectaddress=YOUR_WSL_IP
netsh advfirewall firewall add rule name="MedVid AR" dir=in action=allow protocol=TCP localport=8000
```

Verify:

```powershell
netsh interface portproxy show all
```

**Note:** WSL IP changes after reboot. Re-run these commands if Quest can't connect.

---

## Step 15: Run the System (3 Terminals)

### Terminal 1 — vLLM (The Brain)

> **Open: Ubuntu terminal**

```bash
cd /mnt/c/Users/YOUR_USERNAME/Documents/LOGS
vllm serve Qwen/Qwen3-VL-8B-Instruct \
  --port 22002 \
  --max-model-len 8192 \
  --gpu-memory-utilization 0.9 \
  --enforce-eager
```

First run downloads ~16GB model. Wait for `Application startup complete.`

### Terminal 2 — Backend Server

> **Open: Second Ubuntu terminal**

```bash
cd /mnt/c/Users/YOUR_USERNAME/Documents/LOGS
python3 logger_server.py
```

First run downloads Whisper model (~3GB). Wait for:

```
INFO: Uvicorn running on http://0.0.0.0:8000
```

### Terminal 3 — ADB

> **Open: Windows Command Prompt**

```cmd
adb reverse tcp:8000 tcp:8000
```

### Use It

Put on Quest 3, launch the Unity app. Hold A for 1 second to record, release to send.

---

## After Reboot Checklist

Every time the PC restarts, you need to:

1. Start Terminal 1 (vLLM) — wait for startup
2. Start Terminal 2 (logger_server.py) — wait for startup
3. Check WSL IP: `wsl hostname -I`
4. Update port proxy if IP changed:
```powershell
netsh interface portproxy delete v4tov4 listenport=8000 listenaddress=0.0.0.0
netsh interface portproxy add v4tov4 listenport=8000 listenaddress=0.0.0.0 connectport=8000 connectaddress=NEW_WSL_IP
```
5. Connect Quest and run: `adb reverse tcp:8000 tcp:8000`

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Quest says "Connection error" | Re-run `adb reverse tcp:8000 tcp:8000` and check port proxy WSL IP |
| `adb devices` shows "unauthorized" | Put on headset, tap "Always Allow" on USB debugging popup |
| `adb devices` shows empty list | Try different USB cable/port, restart ADB with `adb kill-server && adb start-server` |
| "No module named search_engine" | Make sure `search_engine.py` is in the same folder as `logger_server.py` |
| "Voices file not found" | Download `voices.bin` from GitHub, rename to `voices.json`, put in `Kokoro/` |
| "Permission denied" on medical_db.json | Run `sed -i 's/OLD_USER/NEW_USER/g' logger_server.py search_engine.py` |
| "CUDA out of memory" | Use `--max-model-len 4096 --gpu-memory-utilization 0.85` for vLLM |
| "espeak not found" | Run `sudo apt install -y espeak-ng espeak-ng-data` |
| Videos have no audio on Quest | Video uses AV1 codec. Re-encode: `ffmpeg -i input.mp4 -c:v libx264 -c:a aac output.mp4` |
| Windows Firewall blocks ADB | Click "Allow" when the firewall popup appears |
