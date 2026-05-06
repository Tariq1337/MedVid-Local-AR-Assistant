// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class DetectionManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [Header("Placement configuration")]
        [SerializeField] private DetectionSpawnMarkerAnim m_spawnMarker;
        [SerializeField] private AudioSource m_placeSound;
        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [SerializeField] private SentisInferenceRunManager m_runManager; 

        [Space(10)]
        public UnityEvent<int> OnObjectsIdentified;

        private readonly List<DetectionSpawnMarkerAnim> m_spawnedEntities = new();
        private bool m_isStarted;
        internal OVRSpatialAnchor m_spatialAnchor;
        private bool m_isHeadsetTracking;

        // --- X BUTTON HOLD STATE ---
        private float m_xButtonHoldTime = 0f;
        private bool m_xLongPressHandled = false;
        private const float LONG_PRESS_THRESHOLD = 1.0f;

        private void Awake()
        {
            StartCoroutine(UpdateSpatialAnchor());
            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;
        }

        private void OnDestroy()
        {
            EraseSpatialAnchor();
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
        }

        private void OnTrackingLost() => m_isHeadsetTracking = false;
        private void OnTrackingAcquired() => m_isHeadsetTracking = true;

        /// <summary>
        /// Public accessor so CameraViewerManager can use the same sound.
        /// </summary>
        public AudioSource PlaceSound => m_placeSound;

        private void Update()
        {
            if (!m_isStarted && m_cameraAccess.IsPlaying) m_isStarted = true;

            // --- X BUTTON: Short press = scan, Long press = clear ---
            if (OVRInput.GetDown(OVRInput.RawButton.X))
            {
                m_xButtonHoldTime = 0f;
                m_xLongPressHandled = false;
            }

            if (OVRInput.Get(OVRInput.RawButton.X))
            {
                m_xButtonHoldTime += Time.deltaTime;

                // Long press: Clear all markers (fire ONCE)
                if (m_xButtonHoldTime >= LONG_PRESS_THRESHOLD && !m_xLongPressHandled)
                {
                    Debug.Log("[DetectionManager] Long press X — Clearing all markers");
                    TriggerSmartScan("");
                    if (m_placeSound != null) m_placeSound.Play();
                    m_xLongPressHandled = true;  // Prevent firing again while still held
                }
            }

            if (OVRInput.GetUp(OVRInput.RawButton.X))
            {
                // Short press: Trigger scan (only if long press didn't already fire)
                if (!m_xLongPressHandled && m_xButtonHoldTime > 0f)
                {
                    Debug.Log("[DetectionManager] Short press X — Scanning objects");
                    TriggerSmartScan("debug_show_all");
                }
                m_xButtonHoldTime = 0f;
                m_xLongPressHandled = false;
            }
        }

        // --- [NEW] INTELLIGENT TRIGGER ---
        public void TriggerSmartScan(string objectList)
        {
            Debug.Log($"[DetectionManager] Received Smart Command: {objectList}");
            
            string cleanList = objectList.ToLower().Trim();

            // 1. Tell UI to Show Green Squares
            if (m_runManager != null) m_runManager.SetHighlight(cleanList);

            // 2. Clear Old Text Markers
            CleanMarkers();

            // 3. Spawn NEW Text Markers (Filtered by list)
            if (!string.IsNullOrEmpty(cleanList))
            {
                SpawnFilteredObjects(cleanList);
            }
        }

        private void SpawnFilteredObjects(string filterList)
        {
            if (m_uiInference == null) return;
            var newCount = 0;

            foreach (SentisInferenceUiManager.BoundingBoxData box in m_uiInference.m_boxDrawn)
            {
                string cls = box.ClassName.ToLower();

                // SPAWN IF: Debug Mode OR List contains this object
                bool shouldSpawn = (filterList == "debug_show_all") || filterList.Contains(cls);

                if (shouldSpawn)
                {
                    var marker = Instantiate(m_spawnMarker, box.BoxRectTransform.position, box.BoxRectTransform.rotation, m_uiInference.ContentParent);
                    marker.GetComponent<DetectionSpawnMarkerAnim>().SetYoloClassName(box.ClassName);
                    m_spawnedEntities.Add(marker);
                    newCount++;
                }
            }
            
            if (newCount > 0) m_placeSound.Play();
            Debug.Log($"[DetectionManager] Spawned {newCount} text markers for '{filterList}'");
            OnObjectsIdentified?.Invoke(newCount);
        }

        // ... Spatial Anchor Logic (Unchanged) ...
        private IEnumerator UpdateSpatialAnchor() { while (true) { yield return null; if (m_spatialAnchor == null) { yield return CreateSpatialAnchorAndSave(); if (m_spatialAnchor == null) continue; } if (!m_spatialAnchor.IsTracked) yield return RestoreSpatialAnchorTracking(); } IEnumerator CreateSpatialAnchorAndSave() { if (m_uiInference == null || m_uiInference.ContentParent == null) yield break; m_spatialAnchor = m_uiInference.ContentParent.gameObject.AddComponent<OVRSpatialAnchor>(); while (true) { if (m_spatialAnchor == null) yield break; if (m_spatialAnchor.Localized) break; yield return null; } var awaiter = m_spatialAnchor.SaveAnchorAsync().GetAwaiter(); while (!awaiter.IsCompleted) yield return null; if (!awaiter.GetResult().Success) { EraseSpatialAnchor(); yield break; } } IEnumerator RestoreSpatialAnchorTracking() { const int numRetries = 5; for (int i = 0; i < numRetries; i++) { if (!m_isHeadsetTracking) yield break; var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>(1); var awaiter = OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { m_spatialAnchor.Uuid }, unboundAnchors).GetAwaiter(); while (!awaiter.IsCompleted) yield return null; if (!awaiter.GetResult().Success || unboundAnchors.Count != 0) { EraseSpatialAnchor(); yield break; } yield return null; if (m_spatialAnchor.IsTracked) yield break; yield return new WaitForSeconds(1f); } EraseSpatialAnchor(); } }

        private void EraseSpatialAnchor()
        {
            if (m_spatialAnchor != null) { m_spatialAnchor.EraseAnchorAsync(); DestroyImmediate(m_spatialAnchor); m_spatialAnchor = null; CleanMarkers(); m_uiInference.ClearAnnotations(); }
        }

        private void CleanMarkers()
        {
            foreach (var e in m_spawnedEntities) if(e != null) Destroy(e.gameObject);
            m_spawnedEntities.Clear();
            OnObjectsIdentified?.Invoke(-1);
        }
    }
}