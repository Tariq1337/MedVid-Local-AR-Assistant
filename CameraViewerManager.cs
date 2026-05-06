using System.Collections;
using System.IO;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.UI;
using UnityEngine.Networking;
using UnityEngine.Video; 
using TMPro; 
using PassthroughCameraSamples.MultiObjectDetection;

namespace PassthroughCameraSamples.CameraViewer
{
    [RequireComponent(typeof(AudioSource))]
    public class CameraViewerManager : MonoBehaviour
    {
        [Header("Setup")]

        [Header("UI - Subtitles")]
        [SerializeField] private TMP_Text m_subtitleText;     
        [SerializeField] private Image m_subtitleBackground;  
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private Text m_debugText;
        [SerializeField] private RawImage m_image;

        [Header("AI Detection Link")]
        [SerializeField] private DetectionManager m_detectionManager;

        [Header("Video Feedback")]
        [SerializeField] private VideoPlayer m_videoPlayer; 
        [SerializeField] private RawImage m_videoScreen;
        [SerializeField] private float m_fadeDuration = 1.5f;
        [SerializeField] private TMP_Text m_videoInfoText;  // Drag 'Video_INFO' TextMeshPro here

        [Header("Audio Feedback")]
        // Uses the same sound as the object tracker (from DetectionManager)
        // No need to assign anything here — it grabs it automatically

        private string serverUrl = "http://127.0.0.1:8000/ask"; 
        private string configPath;

        private AudioSource m_audioSource;
        private bool m_isRecording = false;
        private AudioClip m_recordedClip;
        private float m_recordingStartTime;
        private Coroutine m_activeTypewriter;

        // --- VIDEO CONTROL STATE ---
        private bool m_isVideoPlaying = false;
        private bool m_isVideoPaused = false;
        private float m_videoStartTime = 0f;
        private float m_videoEndTime = 0f;
        private string m_videoTopicName = "";
        private Coroutine m_activeVideoCoroutine;
        private float m_bButtonHoldTime = 0f;
        private const float LONG_PRESS_THRESHOLD = 1.0f;
        private const float SKIP_SECONDS = 5.0f;

        // --- SKIP FLASH (temporary text overlay on timer line) ---
        private string m_skipFlashText = "";
        private float m_skipFlashTimer = 0f;
        private const float SKIP_FLASH_DURATION = 1.0f;

        // --- TRIGGER SKIP COOLDOWN ---
        private float m_lastSkipTime = 0f;
        private const float SKIP_COOLDOWN = 0.5f;
        private bool m_leftTriggerWasReleased = true;
        private bool m_rightTriggerWasReleased = true;

        // --- A BUTTON HOLD-TO-RECORD ---
        private float m_aButtonHoldTime = 0f;
        private bool m_recordingArmed = false;
        private const float RECORD_HOLD_THRESHOLD = 1.0f;

        // ============================================================
        //  START
        // ============================================================
        private IEnumerator Start()
        {
            if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageRead))
            {
                Permission.RequestUserPermission(Permission.ExternalStorageRead);
            }

            m_audioSource = GetComponent<AudioSource>();

            // --- CONFIG LOGIC ---
            configPath = Path.Combine(Application.persistentDataPath, "server_config.txt");
            if (File.Exists(configPath))
            {
                serverUrl = File.ReadAllText(configPath).Trim();
                Debug.Log("[Config] Loaded Server URL: " + serverUrl);
            }
            else
            {
                File.WriteAllText(configPath, serverUrl);
                Debug.Log("[Config] Created new config with default URL.");
            }

            // Hide video screen and info text on start
            if (m_videoScreen != null)
            {
                Color c = m_videoScreen.color;
                c.a = 0f;
                m_videoScreen.color = c;
            }
            if (m_videoInfoText != null) m_videoInfoText.text = "";

            // Clear subtitles
            if (m_subtitleText != null) m_subtitleText.text = "";
            if (m_subtitleBackground != null) m_subtitleBackground.enabled = false;

            // --- PRE-CONFIGURE VIDEO PLAYER AUDIO AT START ---
            if (m_videoPlayer != null)
            {
                m_videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
                m_videoPlayer.EnableAudioTrack(0, true);
                m_videoPlayer.SetTargetAudioSource(0, m_audioSource);
                m_videoPlayer.controlledAudioTrackCount = 1;
            }

            // Standard camera setup
            while (!m_cameraAccess.IsPlaying) yield return null;
            m_image.texture = m_cameraAccess.GetTexture();
            m_debugText.text = $"READY.\nHold A: Speak | B: Video Controls";

            if (m_detectionManager != null) m_detectionManager.TriggerSmartScan("");
        }

        // ============================================================
        //  UPDATE
        // ============================================================
        private void Update()
        {
            // --- A BUTTON: Hold to Record ---
            HandleRecordButton();

            // --- VIDEO CONTROLS (B + Triggers) ---
            if (m_isVideoPlaying)
            {
                HandleVideoControls();
                UpdateVideoTimer();
            }
        }

        // ============================================================
        //  LIVE VIDEO TIMER (YouTube-style display on Video_INFO)
        // ============================================================
        private void UpdateVideoTimer()
        {
            if (m_videoInfoText == null || m_videoPlayer == null) return;

            double currentTime = m_videoPlayer.time;
            double totalDuration = m_videoEndTime - m_videoStartTime;
            double remaining = m_videoEndTime - currentTime;
            if (remaining < 0) remaining = 0;
            if (totalDuration <= 0) totalDuration = 1;

            // --- LINE 1: Topic name ---
            string topicLine = m_videoTopicName;

            // --- LINE 2: Timer + Progress bar + Remaining ---
            string currentStr = FormatTimeMMSS(currentTime);
            string totalStr = FormatTimeMMSS(totalDuration);
            string progressBar = BuildProgressBar(currentTime - m_videoStartTime, totalDuration, 20);

            // Skip flash: temporarily replace remaining text with skip notification
            string rightInfo;
            if (m_skipFlashTimer > 0f)
            {
                m_skipFlashTimer -= Time.deltaTime;
                rightInfo = m_skipFlashText;
            }
            else if (m_isVideoPaused)
            {
                rightInfo = "PAUSED";
            }
            else
            {
                rightInfo = $"{FormatTimeShort(remaining)} left";
            }

            string timerLine = $"{currentStr} / {totalStr}  {progressBar}  {rightInfo}";

            // --- LINE 3: Controls (YouTube layout: rewind - play/pause - forward) ---
            string pauseIcon = m_isVideoPaused ? "B:[>>]" : "B:[||]";
            string controlsLine = $"L:[<<]  {pauseIcon}  R:[>>]";
            if (m_isVideoPaused) controlsLine += "  Hold B:[X]";

            // --- COMBINE ---
            m_videoInfoText.text = $"{topicLine}\n{timerLine}\n{controlsLine}";

            // --- COLOR: green normally, yellow under 30s, red under 10s ---
            if (m_isVideoPaused)
                m_videoInfoText.color = Color.yellow;
            else if (remaining <= 10)
                m_videoInfoText.color = new Color(1f, 0.3f, 0.3f); // Red
            else if (remaining <= 30)
                m_videoInfoText.color = new Color(1f, 0.9f, 0.3f); // Yellow
            else
                m_videoInfoText.color = Color.white;
        }

        private string FormatTimeMMSS(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int totalSec = Mathf.FloorToInt((float)seconds);
            int min = totalSec / 60;
            int sec = totalSec % 60;
            return $"{min:D2}:{sec:D2}";
        }

        private string FormatTimeShort(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int totalSec = Mathf.FloorToInt((float)seconds);
            int min = totalSec / 60;
            int sec = totalSec % 60;
            if (min > 0)
                return $"{min}m {sec:D2}s";
            else
                return $"{sec}s";
        }

        private string BuildProgressBar(double elapsed, double total, int barLength)
        {
            if (total <= 0) return "[" + new string('-', barLength) + "]";
            float progress = Mathf.Clamp01((float)(elapsed / total));
            int filled = Mathf.RoundToInt(progress * barLength);
            int empty = barLength - filled;
            return "[" + new string('=', filled) + new string('-', empty) + "]";
        }

        // ============================================================
        //  A BUTTON: Hold-to-Record
        // ============================================================
        private void HandleRecordButton()
        {
            if (OVRInput.GetDown(OVRInput.Button.One))
            {
                m_aButtonHoldTime = 0f;
                m_recordingArmed = false;
            }

            if (OVRInput.Get(OVRInput.Button.One))
            {
                m_aButtonHoldTime += Time.deltaTime;

                if (!m_recordingArmed && !m_isRecording)
                {
                    if (m_aButtonHoldTime < RECORD_HOLD_THRESHOLD)
                    {
                        float remaining = RECORD_HOLD_THRESHOLD - m_aButtonHoldTime;
                        ShowSubtitle("SYSTEM", $"Hold A to speak... {remaining:F1}s", Color.gray, false);
                    }
                    else
                    {
                        m_recordingArmed = true;
                        PlayClickSound();
                        StartRecording();
                    }
                }
            }

            if (OVRInput.GetUp(OVRInput.Button.One))
            {
                if (m_aButtonHoldTime < RECORD_HOLD_THRESHOLD)
                {
                    ShowSubtitle("SYSTEM", "Cancelled", Color.gray, false);
                    StartCoroutine(ClearSubtitleAfterDelay(1.0f));
                }
                else
                {
                    StopRecordingAndCapture();
                }
                m_aButtonHoldTime = 0f;
                m_recordingArmed = false;
            }
        }

        // ============================================================
        //  VIDEO CONTROLS (B Button + Index Triggers)
        // ============================================================
        private void HandleVideoControls()
        {
            // --- B BUTTON: Pause/Resume (short) or Dismiss (long) ---
            if (OVRInput.GetDown(OVRInput.Button.Two))
            {
                m_bButtonHoldTime = 0f;
            }

            if (OVRInput.Get(OVRInput.Button.Two))
            {
                m_bButtonHoldTime += Time.deltaTime;

                if (m_bButtonHoldTime > 0.5f && m_bButtonHoldTime < LONG_PRESS_THRESHOLD)
                {
                    ShowSubtitle("SYSTEM", "Hold to dismiss video...", Color.red, false);
                }

                if (m_bButtonHoldTime >= LONG_PRESS_THRESHOLD)
                {
                    PlayClickSound();
                    DismissVideo();
                    m_bButtonHoldTime = 0f;
                    return;
                }
            }

            if (OVRInput.GetUp(OVRInput.Button.Two))
            {
                if (m_bButtonHoldTime < LONG_PRESS_THRESHOLD)
                {
                    PlayClickSound();
                    TogglePauseVideo();
                }
                m_bButtonHoldTime = 0f;
            }

            // --- INDEX TRIGGERS: Skip forward/backward ---
            // Left trigger (index finger) = Rewind 5s
            // Right trigger (index finger) = Forward 5s
            float leftTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger);    // Left hand
            float rightTrigger = OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger);  // Right hand
            float now = Time.time;

            // LEFT TRIGGER → Rewind
            if (leftTrigger > 0.7f)
            {
                if (m_leftTriggerWasReleased && (now - m_lastSkipTime) > SKIP_COOLDOWN)
                {
                    PlayClickSound();
                    SkipVideo(-SKIP_SECONDS);
                    m_lastSkipTime = now;
                    m_leftTriggerWasReleased = false;
                    Debug.Log($"[Controls] LEFT TRIGGER → Rewind {SKIP_SECONDS}s");
                }
            }
            else if (leftTrigger < 0.3f)
            {
                m_leftTriggerWasReleased = true;
            }

            // RIGHT TRIGGER → Forward
            if (rightTrigger > 0.7f)
            {
                if (m_rightTriggerWasReleased && (now - m_lastSkipTime) > SKIP_COOLDOWN)
                {
                    PlayClickSound();
                    SkipVideo(SKIP_SECONDS);
                    m_lastSkipTime = now;
                    m_rightTriggerWasReleased = false;
                    Debug.Log($"[Controls] RIGHT TRIGGER → Forward {SKIP_SECONDS}s");
                }
            }
            else if (rightTrigger < 0.3f)
            {
                m_rightTriggerWasReleased = true;
            }
        }

        // ============================================================
        //  VIDEO CONTROL ACTIONS
        // ============================================================
        private void PlayClickSound()
        {
            // Use the same sound as the object detection tracker
            if (m_detectionManager != null && m_detectionManager.PlaceSound != null)
            {
                m_detectionManager.PlaceSound.Play();
            }
        }

        /// <summary>
        /// Shows a notification on the subtitle (ChatHistory) only.
        /// Video_INFO is handled by UpdateVideoTimer continuously.
        /// </summary>
        private void ShowVideoNotification(string message, Color color, float clearDelay = 1.5f)
        {
            ShowSubtitle("SYSTEM", message, color, false);
            StartCoroutine(ClearNotificationAfterDelay(clearDelay));
        }

        IEnumerator ClearNotificationAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (m_subtitleText != null) m_subtitleText.text = "";
            if (m_subtitleBackground != null) m_subtitleBackground.enabled = false;
        }

        private void TogglePauseVideo()
        {
            if (m_videoPlayer == null) return;

            if (m_isVideoPaused)
            {
                m_videoPlayer.Play();
                m_audioSource.UnPause();
                m_isVideoPaused = false;
                ShowVideoNotification("[>>] Playing", Color.green);
                Debug.Log("[Controls] Video resumed");
            }
            else
            {
                m_videoPlayer.Pause();
                m_audioSource.Pause();
                m_isVideoPaused = true;
                double currentTime = m_videoPlayer.time;
                ShowVideoNotification($"[||] Paused at {FormatTimeMMSS(currentTime)}", Color.yellow, 0f);
                Debug.Log($"[Controls] Video paused at {currentTime:F1}s");
            }
        }

        private void SkipVideo(float seconds)
        {
            if (m_videoPlayer == null || !m_isVideoPlaying) return;

            double newTime = m_videoPlayer.time + seconds;
            if (newTime < m_videoStartTime) newTime = m_videoStartTime;
            if (newTime > m_videoEndTime) newTime = m_videoEndTime - 1;

            m_videoPlayer.time = newTime;

            // Set flash text (replaces "Xs left" on the timer line for 1 second)
            string direction = seconds > 0 ? ">> +" : "<< -";
            m_skipFlashText = $"{direction}{Mathf.Abs(seconds)}s";
            m_skipFlashTimer = SKIP_FLASH_DURATION;

            // Also show on subtitle
            string label = seconds > 0 ? "Forward" : "Rewind";
            ShowVideoNotification($"{label} {Mathf.Abs(seconds)}s → {FormatTimeMMSS(newTime)}", Color.cyan, 1.0f);

            Debug.Log($"[Controls] {label} {Mathf.Abs(seconds)}s → {newTime:F1}s");
        }

        private void DismissVideo()
        {
            if (m_activeVideoCoroutine != null)
            {
                StopCoroutine(m_activeVideoCoroutine);
                m_activeVideoCoroutine = null;
            }

            if (m_videoPlayer != null) m_videoPlayer.Stop();
            if (m_audioSource != null) m_audioSource.Stop();

            // Hide video screen
            if (m_videoScreen != null)
            {
                Color c = m_videoScreen.color;
                c.a = 0f;
                m_videoScreen.color = c;
            }

            // Hide Video_INFO completely
            if (m_videoInfoText != null)
            {
                m_videoInfoText.text = "";
                m_videoInfoText.gameObject.SetActive(false);
            }

            m_isVideoPlaying = false;
            m_isVideoPaused = false;
            m_debugText.text = "";

            ShowSubtitle("SYSTEM", "Video dismissed", Color.gray, false);
            StartCoroutine(ClearSubtitleAfterDelay(1.5f));

            Debug.Log("[Controls] Video dismissed by user");
        }

        IEnumerator ClearSubtitleAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (m_subtitleText != null) m_subtitleText.text = "";
            if (m_subtitleBackground != null) m_subtitleBackground.enabled = false;
        }

        // ============================================================
        //  DETECTION HIGHLIGHTS
        // ============================================================
        public void TriggerSmartHighlight(string objectList)
        {
            if (m_detectionManager != null)
            {
                m_debugText.text = "AI Found: " + objectList;
                m_detectionManager.TriggerSmartScan(objectList);
                StartCoroutine(ClearHighlightAfterDelay(15f));
            }
        }

        IEnumerator ClearHighlightAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (m_detectionManager != null) m_detectionManager.TriggerSmartScan("");
        }

        // ============================================================
        //  SUBTITLES
        // ============================================================
        private void ShowSubtitle(string speaker, string message, Color color, bool useTypewriter = false)
        {
            if (m_subtitleText == null) return;
            if (m_subtitleBackground != null) m_subtitleBackground.enabled = true;
            if (m_activeTypewriter != null) StopCoroutine(m_activeTypewriter);

            if (useTypewriter)
                m_activeTypewriter = StartCoroutine(TypewriterRoutine(speaker, message, color));
            else
            {
                m_subtitleText.text = $"{speaker}: {message}";
                m_subtitleText.color = color;
            }
        }

        IEnumerator TypewriterRoutine(string speaker, string message, Color color)
        {
            m_subtitleText.color = color;
            m_subtitleText.text = $"{speaker}: "; 
            foreach (char letter in message.ToCharArray())
            {
                m_subtitleText.text += letter;
                yield return new WaitForSeconds(0.04f); 
            }
            yield return new WaitForSeconds(3.0f);
            m_subtitleText.text = "";
            if (m_subtitleBackground != null) m_subtitleBackground.enabled = false;
        }

        // ============================================================
        //  RECORDING
        // ============================================================
        private void StartRecording() 
        { 
            if (m_isRecording) return; 
            if (m_debugText != null) m_debugText.text = ""; 

            string mic = Microphone.devices.Length > 0 ? Microphone.devices[0] : null; 
            if (mic == null) return; 
            m_isRecording = true; 
            m_recordedClip = Microphone.Start(mic, false, 30, 44100); 
            m_recordingStartTime = Time.time; 
            
            ShowSubtitle("User", "Listening...", Color.yellow, false);
        }

        private void StopRecordingAndCapture() 
        { 
            if (!m_isRecording) return; 
            m_isRecording = false; 
            Microphone.End(null); 
            float duration = Time.time - m_recordingStartTime; 
            if (duration < 0.5f) return; 
            
            ShowSubtitle("SYSTEM", "Sending to Brain...", Color.gray, false);
            StartCoroutine(CaptureAndSend(duration)); 
        }

        // ============================================================
        //  CAPTURE & SEND
        // ============================================================
        IEnumerator CaptureAndSend(float audioDuration)
        {
            yield return new WaitForEndOfFrame();
            Texture mainTexture = m_image.texture;
            Texture2D snapshot = new Texture2D(mainTexture.width, mainTexture.height, TextureFormat.RGB24, false);
            RenderTexture tempRT = RenderTexture.GetTemporary(mainTexture.width, mainTexture.height, 0);
            Graphics.Blit(mainTexture, tempRT);
            RenderTexture.active = tempRT;
            snapshot.ReadPixels(new Rect(0, 0, tempRT.width, tempRT.height), 0, 0);
            snapshot.Apply();
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(tempRT);
            byte[] imageBytes = snapshot.EncodeToJPG();
            Destroy(snapshot);
            AudioClip trimmedClip = TrimAudio(m_recordedClip, audioDuration);
            byte[] audioBytes = EncodeToWAV(trimmedClip);
            StartCoroutine(UploadData(imageBytes, audioBytes));
        }

        // ============================================================
        //  UPLOAD & HANDLE RESPONSE
        // ============================================================
        IEnumerator UploadData(byte[] imageBytes, byte[] audioBytes)
        {
            WWWForm form = new WWWForm();
            form.AddBinaryData("image", imageBytes, "view.jpg", "image/jpeg");
            form.AddBinaryData("audio", audioBytes, "question.wav", "audio/wav");

            using (UnityWebRequest www = UnityWebRequest.Post(serverUrl, form))
            {
                www.downloadHandler = new DownloadHandlerAudioClip(serverUrl, AudioType.WAV);
                www.timeout = 30;

                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    AudioClip aiAudio = DownloadHandlerAudioClip.GetContent(www);
                    string userText = www.GetResponseHeader("X-User-Transcript");
                    string aiText = www.GetResponseHeader("X-AI-Response");
                    string detectedObj = www.GetResponseHeader("X-Detected-Object");
                    string videoFilename = www.GetResponseHeader("X-Video-URL");
                    string videoStartStr = www.GetResponseHeader("X-Video-Start");
                    string videoEndStr = www.GetResponseHeader("X-Video-End");
                    string videoTitle = www.GetResponseHeader("X-Video-Title");

                    Debug.Log($"[Response] User: {userText}");
                    Debug.Log($"[Response] AI: {aiText}");
                    Debug.Log($"[Response] Video: {videoFilename} | Start: {videoStartStr} | End: {videoEndStr} | Title: {videoTitle}");

                    // --- 1. SHOW USER TEXT ---
                    if (!string.IsNullOrEmpty(userText))
                    {
                        ShowSubtitle("User", userText, Color.yellow, true);
                        float waitTime = (userText.Length * 0.04f) + 1.5f;
                        yield return new WaitForSeconds(waitTime);
                    }

                    // --- 2. PLAY AI VOICE ---
                    if (aiAudio != null)
                    {
                        m_audioSource.clip = aiAudio;
                        m_audioSource.Play();
                    }

                    // --- 3. SHOW AI SUBTITLE ---
                    if (!string.IsNullOrEmpty(aiText))
                    {
                        ShowSubtitle("AI", aiText, Color.green, true);
                    }

                    // --- 4. TRIGGER DETECTION HIGHLIGHTS ---
                    if (!string.IsNullOrEmpty(detectedObj) && detectedObj != "none")
                    {
                        TriggerSmartHighlight(detectedObj);
                    }

                    // --- 5. WAIT FOR AI VOICE TO FINISH ---
                    if (aiAudio != null)
                    {
                        yield return new WaitForSeconds(aiAudio.length + 0.5f);
                        m_audioSource.Stop();
                        m_audioSource.clip = null;
                    }

                    // --- 6. PLAY MEDICAL VIDEO (AFTER voice finishes) ---
                    if (!string.IsNullOrEmpty(videoFilename) && videoFilename != "")
                    {
                        float startSec = 0f;
                        float endSec = 0f;
                        float.TryParse(videoStartStr, out startSec);
                        float.TryParse(videoEndStr, out endSec);

                        // Use AI-generated video title, fallback to user's question
                        if (!string.IsNullOrEmpty(videoTitle))
                            m_videoTopicName = videoTitle;
                        else if (!string.IsNullOrEmpty(userText))
                            m_videoTopicName = userText;
                        else
                            m_videoTopicName = "Medical Video";

                        m_activeVideoCoroutine = StartCoroutine(PlayMedicalVideo(videoFilename, startSec, endSec));
                    }
                }
                else
                {
                    Debug.LogError("Server Error: " + www.error);
                    ShowSubtitle("ERROR", "Connection Failed. Check IP.", Color.red, false);
                }
            }
        }

        // ============================================================
        //  MEDICAL VIDEO PLAYBACK
        // ============================================================
        IEnumerator PlayMedicalVideo(string filename, float startTime, float endTime)
        {
            if (m_videoPlayer == null || m_videoScreen == null)
            {
                Debug.LogError("[Video] VideoPlayer or VideoScreen not assigned!");
                yield break;
            }

            // --- FIND FILE ---
            string[] searchPaths = {
                "/sdcard/Movies/MedicalVideos/" + filename,
                "/mnt/sdcard/Movies/MedicalVideos/" + filename,
                "/storage/emulated/0/Movies/MedicalVideos/" + filename
            };

            string validatedPath = "";
            foreach (string path in searchPaths)
            {
                if (File.Exists(path))
                {
                    validatedPath = path;
                    break;
                }
            }

            if (string.IsNullOrEmpty(validatedPath))
            {
                Debug.LogError($"[Video] NOT FOUND: {filename}");
                m_debugText.text = $"Video missing: {filename}";
                yield break;
            }

            // --- RESET ---
            m_videoPlayer.Stop();
            m_audioSource.Stop();
            m_audioSource.clip = null;
            m_audioSource.volume = 1.0f;
            yield return null;

            // --- CONFIGURE ---
            m_videoPlayer.source = VideoSource.Url;
            m_videoPlayer.url = "file://" + validatedPath;

            // --- PREPARE ---
            m_videoPlayer.Prepare();
            float waitTimer = 0f;
            while (!m_videoPlayer.isPrepared)
            {
                waitTimer += Time.deltaTime;
                if (waitTimer > 10.0f)
                {
                    Debug.LogError($"[Video] TIMEOUT: {filename}");
                    yield break;
                }
                yield return null;
            }

            // --- SET CONTROL STATE ---
            m_videoStartTime = startTime;
            m_videoEndTime = endTime;
            m_isVideoPlaying = true;
            m_isVideoPaused = false;

            // Enable Video_INFO display
            if (m_videoInfoText != null) m_videoInfoText.gameObject.SetActive(true);

            // Show controls hint on subtitle only (Video_INFO shows the timer)
            PlayClickSound();
            ShowVideoNotification("L:[<<]  B:[||]  R:[>>]  |  Hold B: Close", Color.cyan, 3.0f);

            // --- SEEK AND PLAY ---
            m_videoPlayer.time = startTime;
            m_videoPlayer.Play();
            yield return new WaitForSeconds(0.2f);

            // --- FADE IN ---
            float fadeTimer = 0f;
            Color screenColor = m_videoScreen.color;
            while (fadeTimer < m_fadeDuration)
            {
                fadeTimer += Time.deltaTime;
                screenColor.a = Mathf.Lerp(0f, 1f, fadeTimer / m_fadeDuration);
                m_videoScreen.color = screenColor;
                yield return null;
            }
            screenColor.a = 1f;
            m_videoScreen.color = screenColor;

            // --- WAIT UNTIL END ---
            while (m_isVideoPlaying)
            {
                if (!m_isVideoPaused && m_videoPlayer.time >= endTime) break;
                if (!m_videoPlayer.isPlaying && !m_isVideoPaused) break;
                yield return null;
            }

            // If dismissed, skip fade out
            if (!m_isVideoPlaying)
            {
                if (m_videoInfoText != null)
                {
                    m_videoInfoText.text = "";
                    m_videoInfoText.gameObject.SetActive(false);
                }
                yield break;
            }

            // --- FADE OUT ---
            fadeTimer = 0f;
            while (fadeTimer < m_fadeDuration)
            {
                fadeTimer += Time.deltaTime;
                screenColor.a = Mathf.Lerp(1f, 0f, fadeTimer / m_fadeDuration);
                m_videoScreen.color = screenColor;
                yield return null;
            }
            screenColor.a = 0f;
            m_videoScreen.color = screenColor;

            // --- CLEANUP ---
            m_videoPlayer.Stop();
            m_audioSource.Stop();
            m_isVideoPlaying = false;
            m_isVideoPaused = false;
            m_activeVideoCoroutine = null;
            if (m_videoInfoText != null)
            {
                m_videoInfoText.text = "";
                m_videoInfoText.gameObject.SetActive(false);
            }
            m_debugText.text = "";
        }

        // ============================================================
        //  AUDIO UTILITIES
        // ============================================================
        private AudioClip TrimAudio(AudioClip clip, float duration)
        {
            int samples = Mathf.FloorToInt(duration * clip.frequency * clip.channels);
            float[] data = new float[samples];
            clip.GetData(data, 0);
            AudioClip newClip = AudioClip.Create("Q", samples, clip.channels, clip.frequency, false);
            newClip.SetData(data, 0);
            return newClip;
        }

        private byte[] EncodeToWAV(AudioClip clip)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    writer.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"));
                    writer.Write(36 + clip.samples * clip.channels * 2);
                    writer.Write(System.Text.Encoding.UTF8.GetBytes("WAVEfmt "));
                    writer.Write(16);
                    writer.Write((ushort)1);
                    writer.Write((ushort)clip.channels);
                    writer.Write(clip.frequency);
                    writer.Write(clip.frequency * clip.channels * 2);
                    writer.Write((ushort)(clip.channels * 2));
                    writer.Write((ushort)16);
                    writer.Write(System.Text.Encoding.UTF8.GetBytes("data"));
                    writer.Write(clip.samples * clip.channels * 2);
                    float[] samples = new float[clip.samples * clip.channels];
                    clip.GetData(samples, 0);
                    foreach (var s in samples)
                        writer.Write((short)(s * short.MaxValue));
                }
                return stream.ToArray();
            }
        }
    }
}
