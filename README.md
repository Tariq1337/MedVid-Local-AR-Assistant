# 🕶️ MedVid AR — Fully Local Medical AR Assistant for Meta Quest 3

### Real-Time Vision + Voice + Medical Video Search — Powered by RTX 4090

A high-performance, fully local mixed reality medical assistant for **Meta Quest 3**. The system sees what you see through the passthrough camera, hears your voice, assesses the medical situation, gives spoken advice, and plays relevant instructional videos from a library of 744 medical videos — **all without sending a single byte to the cloud**.

> ✅ 100% offline — no cloud, no API keys, no internet required  
> ✅ Real-time vision + voice loop (camera frame + audio → spoken response)  
> ✅ 744 medical videos with transcript-based follow-up Q&A  
> ✅ Three-phase AI pipeline: Assess → Search → Respond  
> ✅ ASU Maroon & Gold Excel mission logging with full session memory

---

## 📌 How It Works

1. **You speak** through the Quest 3 headset microphone
2. **The AI sees** what you see through the passthrough camera
3. **It assesses** the situation — is this medical? does the image match the complaint?
4. **It responds** with spoken advice and offers a relevant instructional video
5. **You control** the video with Quest controllers (pause, rewind, skip, dismiss)

---

## 🧠 The Local AI Stack

| Component | Technology | Role |
|-----------|-----------|------|
| **Brain** | Qwen/Qwen3-VL-8B-Instruct via vLLM | Vision-language reasoning (sees camera + hears speech) |
| **Ears** | Faster-Whisper large-v3 (CUDA) | Real-time speech-to-text |
| **Mouth** | Kokoro TTS ONNX (af_bella) | Natural text-to-speech synthesis |
| **Search** | MedVidSearch (custom) | 2,566 questions, 744 videos, synonym expansion, LLM reranking |
| **Memory** | Excel mission log | Last 10 conversations loaded as context, full history on recall |
| **Bridge** | ADB reverse tunnel | Quest 3 ↔ local Python server |

---

## 🗺️ Architecture

```
Quest 3 (Unity)                         Windows PC (RTX 4090)
┌─────────────────┐                     ┌──────────────────────────────┐
│ Camera Frame    │──── HTTP POST ────→ │ FastAPI Server (:8000)       │
│ Microphone Audio│    /ask             │                              │
│                 │                     │ 1. Faster-Whisper → text     │
│ ← WAV audio ───│←── FileResponse ──← │ 2. Qwen3-VL → assessment    │
│ ← HTTP headers  │    + headers        │ 3. MedVidSearch → video      │
│   (video info)  │                     │ 4. Qwen3-VL → response      │
│                 │                     │ 5. Kokoro TTS → speech       │
│ Video Player   │                     │ 6. Excel → log               │
│ Object Detection│                     └──────────────────────────────┘
└─────────────────┘
```

---

## 🏥 Three-Phase AI Pipeline

### Phase 1: Assessment
The AI receives the camera image and transcribed speech simultaneously. It produces a structured JSON assessment:

```json
{
  "intent": "medical | vision | general | conversation",
  "what_i_see": "A hand with a small cut on the index finger",
  "visual_matches_complaint": true,
  "severity": "minor | moderate | serious | emergency",
  "needs_video": true,
  "search_topic": "how to clean and bandage a minor finger cut",
  "video_title": "Minor Cut Treatment"
}
```

**Image verification**: The AI independently describes what it sees BEFORE comparing to the user's words. If the user says "my hand is bleeding" but the camera shows a normal hand, the AI honestly reports the mismatch instead of hallucinating blood.

### Phase 2: Search
Runs only when the AI says a video is needed AND the visual complaint is confirmed (or it's a knowledge question). Uses the AI's refined search topic with synonym expansion across 51 medical term groups.

### Phase 3: Response
Context-aware spoken response using severity-appropriate prompts:
- **Medical + visible injury** → advice + loads video
- **Medical + no visible injury** → asks user to show the area
- **Knowledge question** → answers + loads video (image irrelevant)
- **Vision** → describes environment + triggers object detection
- **General/Conversation** → answers normally

---

## 🎮 Quest 3 Controls

| Button | Action |
|--------|--------|
| **A (hold 1s)** | Start voice recording |
| **A (release)** | Stop recording, send to server |
| **B (tap)** | Pause / resume video |
| **B (hold 1s)** | Dismiss video |
| **Left trigger** | Rewind 5 seconds |
| **Right trigger** | Forward 5 seconds |
| **X (tap)** | Scan objects (YOLO detection) |
| **X (hold)** | Clear detection markers |

### Video Info Display
A YouTube-style overlay appears below the video showing:
- AI-generated topic title (e.g., "Nosebleed Treatment Guide")
- Live timer: `01:23 / 03:45 [========------] 2m 22s left`
- Color-coded countdown: white → yellow (<30s) → red (<10s)

---

## 📁 Repository Structure

```
├── logger_server.py          # Main FastAPI backend
├── search_engine.py          # MedVidSearch (synonym expansion + LLM reranking)
├── CameraViewerManager.cs    # Unity client (camera, voice, video, controls)
├── DetectionManager.cs       # Unity YOLO object detection + AR markers
├── requirements.txt          # Python dependencies
├── MedVid_DATA/
│   ├── videos/               # 744 medical .mp4 files
│   ├── transcripts.json      # Full transcripts for all 744 videos
│   └── medical_db.json       # 2,566 questions mapped to video segments
├── Kokoro/
│   ├── kokoro-v0_19.onnx     # TTS model
│   └── voices.json           # Voice configuration
├── Mission_Logs/             # Date-organized session folders
│   └── YYYY-MM-DD/
│       └── YYYY-MM-DD_HH-MM-SS/
│           ├── view_HH-MM-SS.jpg
│           ├── user_HH-MM-SS.wav
│           └── ai_HH-MM-SS.wav
└── mission_log.xlsx          # ASU-styled Excel log with hyperlinks
```

---

## 🛠️ Installation

### Prerequisites
- NVIDIA RTX 4090 (or equivalent with ≥24GB VRAM)
- Ubuntu / WSL2 on Windows
- Meta Quest 3 with developer mode enabled
- ADB installed

### 1. System Packages
```bash
sudo apt update && sudo apt install -y ffmpeg espeak-ng-data
```

### 2. Python Dependencies
```bash
pip install -r requirements.txt
```

### 3. vLLM Model
```bash
pip install vllm
# Model downloads automatically on first run
```

---

## 🚀 Running the System

### Terminal 1 — Vision Model
```bash
vllm serve Qwen/Qwen3-VL-8B-Instruct \
  --port 22002 \
  --max-model-len 8192 \
  --gpu-memory-utilization 0.9 \
  --enforce-eager
```

### Terminal 2 — Backend Server
```bash
python3 logger_server.py
```

### Terminal 3 — Quest 3 Connection
```bash
adb reverse tcp:8000 tcp:8000
```

Then launch the Unity app on the Quest 3. Hold A to speak, release to send.

---

## 📊 Mission Log

The system generates `mission_log.xlsx` styled in ASU Maroon (#8C1D40) & Gold (#FFC627):

| Date | Time | Image | Video Used | Detected Items | User Question | AI Assessment | AI Answer | User Audio | AI Audio |
|------|------|-------|------------|----------------|---------------|---------------|-----------|------------|----------|

- **Image/Audio columns** are clickable hyperlinks to session files
- **Video Used** links directly to the local .mp4 file
- **AI Assessment** contains multi-line structured output (intent, what AI sees, visual match, severity)
- **Memory** loads last 10 rows into every AI prompt; deep scan triggered by recall keywords

---

## 📹 Video Transcript System

All 744 video transcripts are loaded at startup. When you ask about a video you just watched:

- ✅ "What was the main point of that video?" → loads transcript, answers accurately
- ✅ "I missed some steps" → summarizes from actual video content
- ❌ "Show me another video" → correctly routes to new search (not transcript)
- ❌ "No thanks" → correctly ignores (not a transcript request)

---

## 🧪 Test Results

| Scenario | Behavior | Video |
|----------|----------|-------|
| "My hand is bleeding" (no blood visible) | Asks user to show the area | None |
| "My finger is bleeding" (cut visible) | Gives advice, loads video | ✅ Wound care |
| "How do I perform CPR?" | Answers question, loads video | ✅ CPR demo |
| "Summarize the video" | Answers from transcript | None |
| Phone showing bleeding hand | Recognizes injury on screen | ✅ Relevant video |
| "What do you see?" | Describes environment | Object detection |

---

## 👤 Credits

**Developer:** Tariq Bahaaldeen — Arizona State University  
**Supervisor:** Professor Hasti Seifi  

**Built with:**
- [Qwen3-VL](https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct) — Vision-Language Model (Alibaba)
- [Faster-Whisper](https://github.com/SYSTRAN/faster-whisper) — Speech-to-Text
- [Kokoro-ONNX](https://github.com/thewh1teagle/kokoro-onnx) — Text-to-Speech
- [vLLM](https://github.com/vllm-project/vllm) — LLM Serving Engine

**License:** MIT
