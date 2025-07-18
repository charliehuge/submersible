using Encounter.Runtime.Creatures;
using UnityEngine;

namespace Encounter.Runtime.Environments
{
    public class WhaleConductor : MonoBehaviour
    {
        [Header("Microphone Settings")]
        [SerializeField] private float volumeThreshold = 0.02f;    // How loud the mic needs to be
        [SerializeField] private float sustainedTime = 2f;        // How long to sustain (2 seconds)
        [SerializeField] private int sampleRate = 44100;          // Audio sample rate
        [SerializeField] private float updateRate = 0.1f;         // How often to check mic (10 times per second)
    
        [Header("Frequency-Based Detection")]
        [SerializeField] private float noiseThreshold = 0.15f;           // How "noisy" the sound needs to be (human voice has noise)
        [SerializeField] private float fundamentalFreqMin = 80f;         // Human voice fundamental range
        [SerializeField] private float fundamentalFreqMax = 400f;        // Human voice fundamental range
        [SerializeField] private int analysisWindowSize = 512;           // Smaller window for faster response
        [SerializeField] private bool showFrequencyDebug = true;         // Show detailed frequency analysis
    
        [Header("Whale Response")]
        [SerializeField] private float pauseDuration = 1f;        // How long whales pause before responding
        [SerializeField] private float chorusSpread = 0.3f;       // Random delay between whale responses (0-0.3s)
        [SerializeField] private bool debugMicrophone = true;     // Show mic levels in console
    
        private AudioClip _microphoneClip;
        private string _microphoneName;
        private bool _isListening;
        private float _sustainStartTime = -1f;
        private bool _hasTriggeredChorus;
        private float _lastVolumeCheck;
    
        // Frequency analysis
        private float[] _analysisBuffer;
        private float[] _previousBuffer;
    
        // Whale management
        private WhaleCallBehavior[] _allWhales;
        private bool _chorusActive;

        private void Start()
        {
            // Find all whales in the scene
            RefreshWhaleList();
        
            // Initialize frequency analysis
            _analysisBuffer = new float[analysisWindowSize];
            _previousBuffer = new float[analysisWindowSize];
        
            // Initialize microphone
            InitializeMicrophone();
        
            Debug.Log($"Whale Conductor initialized! Found {_allWhales.Length} whales.");
            Debug.Log($"Using noise-based detection: Human voice (noisy) vs FM synth (pure)");
        }

        private void Update()
        {
            // Check microphone input at specified rate
            if (Time.time - _lastVolumeCheck >= updateRate)
            {
                CheckMicrophoneInput();
                _lastVolumeCheck = Time.time;
            }
        
            // Refresh whale list periodically (in case whales are spawned/destroyed)
            if (Time.time % 5f < updateRate) // Every 5 seconds
            {
                RefreshWhaleList();
            }
        }

        private void InitializeMicrophone()
        {
            // Get the default microphone
            if (Microphone.devices.Length > 0)
            {
                _microphoneName = Microphone.devices[0];
                Debug.Log($"Using microphone: {_microphoneName}");
            
                // Start recording from microphone
                _microphoneClip = Microphone.Start(_microphoneName, true, 1, sampleRate);
                _isListening = true;
            }
            else
            {
                Debug.LogError("No microphone detected! Conductor system disabled.");
                enabled = false;
            }
        }

        private void CheckMicrophoneInput()
        {
            if (!_isListening || _microphoneClip == null) return;
        
            // Get current microphone position
            int micPosition = Microphone.GetPosition(_microphoneName);
            if (micPosition < 0) return;
        
            // Calculate the number of samples to analyze
            int startPosition = micPosition - analysisWindowSize;
            if (startPosition < 0) return;
        
            // Get audio data from microphone
            _microphoneClip.GetData(_analysisBuffer, startPosition);
        
            // Analyze the audio characteristics
            bool isHumanVoice = IsLikelyHumanVoice(_analysisBuffer);
            float rms = CalculateRms(_analysisBuffer);
        
            if (debugMicrophone && Time.time % 0.5f < updateRate)
            {
                Debug.Log($"Vol: {rms:F4} | Human: {isHumanVoice} | Threshold: {volumeThreshold:F4}");
            }
        
            // Check if volume is above threshold AND it sounds like human voice
            if (rms >= volumeThreshold && isHumanVoice)
            {
                // Start or continue sustain timer
                if (_sustainStartTime < 0)
                {
                    _sustainStartTime = Time.time;
                    _hasTriggeredChorus = false;
                    Debug.Log("🎤 Human voice detected! Keep singing...");
                }
            
                // Check if we've sustained long enough
                float sustainedDuration = Time.time - _sustainStartTime;
                if (sustainedDuration >= sustainedTime && !_hasTriggeredChorus)
                {
                    TriggerWhaleChorus();
                    _hasTriggeredChorus = true;
                }
            }
            else
            {
                // Reset sustain timer if voice not detected
                if (_sustainStartTime >= 0)
                {
                    float sustainedDuration = Time.time - _sustainStartTime;
                    if (sustainedDuration < sustainedTime)
                    {
                        string reason = !isHumanVoice ? "too pure (not human voice)" : "volume too low";
                        if (showFrequencyDebug)
                        {
                            Debug.Log($"Voice lost ({sustainedDuration:F1}s) - {reason}");
                        }
                    }
                    _sustainStartTime = -1f;
                }
            }
        
            // Store current buffer for next comparison
            System.Array.Copy(_analysisBuffer, _previousBuffer, analysisWindowSize);
        }

        private float CalculateRms(float[] audioData)
        {
            float sum = 0f;
            foreach (var t in audioData)
            {
                sum += t * t;
            }
            return Mathf.Sqrt(sum / audioData.Length);
        }

        private bool IsLikelyHumanVoice(float[] audioData)
        {
            // Human voices have noise/irregularities, FM synths are mathematically perfect
        
            // 1. Calculate "noise" - how much the signal deviates from being perfectly smooth
            float noiseLevel = CalculateNoiseLevel(audioData);
        
            // 2. Check frequency range (basic sanity check)
            float dominantFreq = EstimateDominantFrequency(audioData);
            bool inHumanRange = dominantFreq >= fundamentalFreqMin && dominantFreq <= fundamentalFreqMax;
        
            // 3. Check for basic energy
            float energy = CalculateRms(audioData);
            bool hasEnoughEnergy = energy > 0.001f;
        
            if (showFrequencyDebug)
            {
                Debug.Log($"Noise: {noiseLevel:F3} (need >{noiseThreshold:F3}) | " +
                          $"Freq: {dominantFreq:F1}Hz | Human range: {inHumanRange} | Energy: {hasEnoughEnergy}");
            }
        
            // Human voice = noisy enough + reasonable frequency + has energy
            return noiseLevel >= noiseThreshold && inHumanRange && hasEnoughEnergy;
        }

        private float CalculateNoiseLevel(float[] audioData)
        {
            // Measure how "rough" or "irregular" the signal is
            // Pure sine waves are very smooth, human voices are noisy
        
            float totalVariation = 0f;
            float totalEnergy = 0f;
        
            for (int i = 2; i < audioData.Length - 2; i++)
            {
                // Calculate how much each sample differs from the smooth trend
                float smoothed = (audioData[i-2] + audioData[i-1] + audioData[i] + audioData[i+1] + audioData[i+2]) / 5f;
                float deviation = Mathf.Abs(audioData[i] - smoothed);
            
                totalVariation += deviation;
                totalEnergy += Mathf.Abs(audioData[i]);
            }
        
            // Normalize by total energy to get relative noise level
            if (totalEnergy < 0.001f) return 0f;
            return totalVariation / totalEnergy;
        }

        private float EstimateDominantFrequency(float[] audioData)
        {
            // Simple zero-crossing rate to estimate fundamental frequency
            int zeroCrossings = 0;
        
            for (int i = 1; i < audioData.Length; i++)
            {
                if ((audioData[i] >= 0f) != (audioData[i-1] >= 0f))
                {
                    zeroCrossings++;
                }
            }
        
            // Convert zero crossings to frequency
            float frequency = zeroCrossings / 2f * sampleRate / audioData.Length;
            return frequency;
        }

        private void RefreshWhaleList()
        {
            _allWhales = FindObjectsByType<WhaleCallBehavior>(FindObjectsSortMode.None);
        }

        private void TriggerWhaleChorus()
        {
            if (_chorusActive) return; // Prevent multiple simultaneous choruses
        
            Debug.Log($"🎵 WHALE CHORUS TRIGGERED! 🎵 Conducting {_allWhales.Length} whales!");
            StartCoroutine(ConductChorus());
        }

        private System.Collections.IEnumerator ConductChorus()
        {
            _chorusActive = true;
        
            // First, pause all whales briefly
            foreach (var whale in _allWhales)
            {
                if (whale != null)
                {
                    whale.PauseForChorus(pauseDuration + chorusSpread);
                }
            }
        
            Debug.Log($"All whales pausing for {pauseDuration} seconds...");
            yield return new WaitForSeconds(pauseDuration);
        
            // Then trigger them all with slight random spread for natural feel
            Debug.Log("🐋 WHALE CHORUS BEGINS! 🐋");
        
            foreach (var whale in _allWhales)
            {
                if (whale != null)
                {
                    // Add random delay for more organic chorus effect
                    float delay = Random.Range(0f, chorusSpread);
                    StartCoroutine(DelayedWhaleCall(whale, delay));
                }
            }
        
            // Wait for chorus to finish before allowing another
            yield return new WaitForSeconds(6f); // Typical call duration + buffer
            _chorusActive = false;
        
            Debug.Log("Whale chorus complete! Ready for next conducting session.");
        }

        private System.Collections.IEnumerator DelayedWhaleCall(WhaleCallBehavior whale, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (whale != null)
            {
                whale.TriggerChorusCall();
            }
        }

        private void OnDestroy()
        {
            // Clean up microphone
            if (_isListening)
            {
                Microphone.End(_microphoneName);
            }
        }
    
        // GUI for debugging
        private void OnGUI()
        {
            if (!debugMicrophone) return;
        
            GUI.Box(new Rect(10, 10, 300, 120), "Whale Conductor");
        
            GUI.Label(new Rect(20, 35, 260, 20), $"Whales found: {_allWhales?.Length ?? 0}");
            GUI.Label(new Rect(20, 55, 260, 20), $"Microphone: {(_isListening ? "Active" : "Inactive")}");
        
            if (_sustainStartTime >= 0)
            {
                float progress = (Time.time - _sustainStartTime) / sustainedTime;
                GUI.Label(new Rect(20, 75, 260, 20), $"🎤 Human Voice: {progress * 100:F1}%");
            
                // Progress bar
                GUI.Box(new Rect(20, 95, 260, 20), "");
                GUI.Box(new Rect(20, 95, 260 * progress, 20), "");
            }
            else
            {
                GUI.Label(new Rect(20, 75, 260, 20), "Sing to conduct the whale chorus!");
            }
        }
    }
}