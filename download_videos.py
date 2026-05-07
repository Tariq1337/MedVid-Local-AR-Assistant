"""
MedVid Video Downloader
Downloads all medical videos referenced in medical_db.json into MedVid_DATA/videos/
Requires: pip install yt-dlp --break-system-packages

Usage: python3 download_videos.py
"""

import json
import os
import subprocess
import sys

DB_FILE = "MedVid_DATA/medical_db.json"
VIDEO_DIR = "MedVid_DATA/videos"
os.makedirs(VIDEO_DIR, exist_ok=True)

with open(DB_FILE, "r", encoding="utf-8") as f:
    db = json.load(f)

# Structure: { "question": { "video_id": { "bounds": [start, end] } } }
video_ids = set()
for question, videos in db.items():
    for vid_id in videos.keys():
        video_ids.add(vid_id)

print(f"Found {len(video_ids)} unique videos in database")

existing = set()
if os.path.exists(VIDEO_DIR):
    for f in os.listdir(VIDEO_DIR):
        if f.endswith(".mp4"):
            existing.add(f.replace(".mp4", ""))

to_download = video_ids - existing
print(f"Already have: {len(existing)}")
print(f"Need to download: {len(to_download)}")

if not to_download:
    print("All videos already downloaded!")
    sys.exit(0)

failed = []
for i, vid_id in enumerate(sorted(to_download), 1):
    output_path = os.path.join(VIDEO_DIR, f"{vid_id}.mp4")
    url = f"https://www.youtube.com/watch?v={vid_id}"
    print(f"\n[{i}/{len(to_download)}] Downloading {vid_id}...")
    try:
        result = subprocess.run([
            "yt-dlp",
            "-f", "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
            "--merge-output-format", "mp4",
            "-o", output_path,
            "--no-playlist",
            "--socket-timeout", "30",
            "--retries", "3",
            url
        ], capture_output=True, text=True, timeout=300)
        if result.returncode == 0 and os.path.exists(output_path):
            size_mb = os.path.getsize(output_path) / (1024 * 1024)
            print(f"   ✅ Done ({size_mb:.1f} MB)")
        else:
            print(f"   ❌ Failed: {result.stderr[:200]}")
            failed.append(vid_id)
    except subprocess.TimeoutExpired:
        print(f"   ❌ Timeout")
        failed.append(vid_id)
    except FileNotFoundError:
        print("❌ yt-dlp not found! Install it first:")
        print("   pip install yt-dlp --break-system-packages")
        sys.exit(1)
    except Exception as e:
        print(f"   ❌ Error: {e}")
        failed.append(vid_id)

print(f"\n{'='*50}")
print(f"Downloaded: {len(to_download) - len(failed)}")
print(f"Failed: {len(failed)}")
if failed:
    with open("failed_downloads.txt", "w") as f:
        f.write("\n".join(failed))
    print("Failed list saved to failed_downloads.txt")
