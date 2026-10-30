using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ValheimVRMod.VRCore;
using ValheimVRMod.VRCore.Backends;
using ValheimVRMod.Utilities;

namespace ValheimVRMod.Scripts
{
    // Controls gesture-based magic casting: player holds off-hand near staff tip to accumulate power,
    // then presses trigger to launch or channel the spell.
    // Features:
    // - Genuine magical visual effects: crystalline incandescent core + swirling orbital particle vortex.
    // - Dynamic element-themed particle and light coloring (Fire, Frost, Nature, Blood, Lightning).
    // - Configurable charge expiry timer (default 7 seconds) with visual flutter warning and dissipation audio.
    // - Zero-allocation runtime performance to eliminate memory leaks and GC overhead.
    public class MagicGestureChargeController : MonoBehaviour
    {
        public static MagicGestureChargeController instance;

        public enum ChargeState
        {
            Idle,
            Accumulating,
            Charged,
            Channeling
        }

        public ChargeState CurrentState { get; private set; } = ChargeState.Idle;
        public float AccumulationProgress { get; private set; } = 0f;
        public bool IsAccumulating => CurrentState == ChargeState.Accumulating;
        public bool IsCharged => CurrentState == ChargeState.Charged;
        public bool IsChanneling => CurrentState == ChargeState.Channeling;
        public bool IsCharging => CurrentState == ChargeState.Accumulating || CurrentState == ChargeState.Charged;

        private float accumulatedTimer = 0f;
        private float accumulatedEitr = 0f;
        private float targetEitrCost = 0f;
        private float passiveFeedTimer = 0f;
        private float graceRemaining = 0f;
        private float fizzleLockoutTimer = 0f;
        private float postCastCooldownTimer = 0f;
        private float chargeExpiryRemaining = 0f;
        private float lastHapticTime = 0f;
        private const float HAPTIC_INTERVAL = 0.075f; // ~13 pulses/sec max to avoid SteamVR buffer backlog

        // Reusable persistent indicators
        private GameObject indicatorObject;
        private MeshRenderer coreRenderer;
        private Material coreMaterial;
        private GameObject rimObject;
        private MeshRenderer rimRenderer;
        private Material rimMaterial;
        private ParticleSystem chargeParticles;
        private Material particleMaterial;
        private Light pointLight;
        private AudioSource audioSource;
        private AudioClip cachedChimeClip;
        private AudioClip cachedDissipateClip;
        private bool attemptedClipLoad = false;

        // Active casting mote indicators
        private class MoteInstance
        {
            public GameObject gameObject;
            public MeshRenderer meshRenderer;
            public Material material;
            public GameObject auraObject;
            public MeshRenderer auraRenderer;
            public Material auraMaterial;
            public Vector3 localOffset;
            public bool isActive;
        }

        private const int MAX_POOLED_MOTES = 10;
        private readonly List<MoteInstance> motePool = new List<MoteInstance>();
        private int activeMotesRemaining = 0;

        private static Texture2D s_softParticleTexture;
        private static Texture2D s_rimGlowTexture;
        private static Mesh s_twoSidedQuadMesh;

        private static Texture2D GetSoftParticleTexture()
        {
            if (s_softParticleTexture != null) return s_softParticleTexture;

            int size = 64;
            s_softParticleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            s_softParticleTexture.wrapMode = TextureWrapMode.Clamp;
            s_softParticleTexture.filterMode = FilterMode.Bilinear;

            float center = (size - 1) * 0.5f;
            float maxRadius = center;
            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    float r = dist / maxRadius;
                    float alpha = 0f;
                    if (r < 1.0f)
                    {
                        alpha = Mathf.Clamp01(1f - r);
                        alpha = Mathf.Pow(alpha, 1.8f);
                    }
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            s_softParticleTexture.SetPixels(pixels);
            s_softParticleTexture.Apply(false, true);
            return s_softParticleTexture;
        }

        private static Texture2D GetRimGlowTexture()
        {
            if (s_rimGlowTexture != null) return s_rimGlowTexture;

            int size = 128;
            s_rimGlowTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            s_rimGlowTexture.wrapMode = TextureWrapMode.Clamp;
            s_rimGlowTexture.filterMode = FilterMode.Bilinear;

            float center = (size - 1) * 0.5f;
            float maxRadius = center;
            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    float r = dist / maxRadius;
                    float alpha = 0f;

                    if (r < 1.0f)
                    {
                        // Translucent in center (~0.08 alpha), curving up to bright opaque rim (~0.95 alpha) at r = 0.88,
                        // then sharply feathering to 0.0 at r = 1.0 to ensure a clean antialiased perimeter.
                        if (r <= 0.88f)
                        {
                            float t = r / 0.88f;
                            alpha = Mathf.Lerp(0.08f, 0.95f, Mathf.Pow(t, 2.5f));
                        }
                        else
                        {
                            float t = (r - 0.88f) / 0.12f;
                            alpha = Mathf.Lerp(0.95f, 0.0f, Mathf.Pow(t, 1.5f));
                        }
                    }
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            s_rimGlowTexture.SetPixels(pixels);
            s_rimGlowTexture.Apply(false, true);
            return s_rimGlowTexture;
        }

        private static Mesh GetTwoSidedQuadMesh()
        {
            if (s_twoSidedQuadMesh != null) return s_twoSidedQuadMesh;

            Mesh mesh = new Mesh();
            mesh.name = "VHVR_TwoSidedQuad";
            mesh.vertices = new Vector3[]
            {
                // Front face
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                // Back face
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f)
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            };
            mesh.triangles = new int[]
            {
                0, 2, 1, 2, 3, 1,
                4, 6, 5, 6, 7, 5
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            s_twoSidedQuadMesh = mesh;
            return s_twoSidedQuadMesh;
        }

        private void Awake()
        {
            instance = this;

            // Root indicator object attached locally
            indicatorObject = new GameObject("VHVR_MagicChargeIndicator");
            indicatorObject.layer = LayerUtils.getWorldspaceUiLayer();

            var unlitMatAsset = VRAssetManager.GetAsset<Material>("Unlit");
            var slashMatAsset = VRAssetManager.GetAsset<Material>("Slash");

            // Inner incandescent energy core (translucent 3D sphere to provide stereoscopic depth in VR)
            GameObject coreObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(coreObject.GetComponent<Collider>());
            coreObject.transform.SetParent(indicatorObject.transform, false);
            coreObject.transform.localScale = Vector3.one * 0.045f;
            coreObject.layer = LayerUtils.getWorldspaceUiLayer();

            coreRenderer = coreObject.GetComponent<MeshRenderer>();
            coreMaterial = unlitMatAsset != null ? Instantiate(unlitMatAsset) : new Material(Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Color"));
            coreMaterial.color = new Color(0.35f, 0.8f, 1f, 0.05f);

            coreRenderer.material = coreMaterial;
            coreRenderer.receiveShadows = false;
            coreRenderer.shadowCastingMode = ShadowCastingMode.Off;
            coreRenderer.lightProbeUsage = LightProbeUsage.Off;
            coreRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            // Outer Fresnel-like rim aura billboard: curved translucency with opaque glowing outer edges
            rimObject = new GameObject("VHVR_MagicRimAura");
            rimObject.transform.SetParent(indicatorObject.transform, false);
            rimObject.layer = LayerUtils.getWorldspaceUiLayer();

            MeshFilter rimMf = rimObject.AddComponent<MeshFilter>();
            rimMf.sharedMesh = GetTwoSidedQuadMesh();

            rimRenderer = rimObject.AddComponent<MeshRenderer>();
            rimMaterial = unlitMatAsset != null ? Instantiate(unlitMatAsset) : new Material(Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Color"));
            rimMaterial.mainTexture = GetRimGlowTexture();
            rimMaterial.color = new Color(0.35f, 0.8f, 1f, 0.85f);

            rimRenderer.material = rimMaterial;
            rimRenderer.receiveShadows = false;
            rimRenderer.shadowCastingMode = ShadowCastingMode.Off;
            rimRenderer.lightProbeUsage = LightProbeUsage.Off;
            rimRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            // Swirling orbital particle vortex with soft circular motes (no squares)
            chargeParticles = indicatorObject.AddComponent<ParticleSystem>();
            var psRenderer = indicatorObject.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Billboard;

            if (slashMatAsset != null)
            {
                particleMaterial = Instantiate(slashMatAsset);
            }
            else
            {
                var pShader = Shader.Find("Particles/Standard Unlit")
                           ?? Shader.Find("Mobile/Particles/Additive")
                           ?? Shader.Find("Particles/Additive")
                           ?? Shader.Find("Unlit/Transparent");
                particleMaterial = new Material(pShader);
            }
            particleMaterial.mainTexture = GetSoftParticleTexture();
            psRenderer.sharedMaterial = particleMaterial;

            var main = chargeParticles.main;
            main.startLifetime = 0.65f;
            main.startSpeed = 0.06f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.026f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 80;
            main.playOnAwake = false;
            main.loop = true;

            var emission = chargeParticles.emission;
            emission.enabled = true;
            emission.rateOverTime = 30f;

            var shape = chargeParticles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.12f;

            var vel = chargeParticles.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;
            vel.orbitalZ = new ParticleSystem.MinMaxCurve(4.0f);
            vel.radial = new ParticleSystem.MinMaxCurve(-0.25f); // Pulls particles inward toward the tip

            var sizeOverLifetime = chargeParticles.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve();
            sizeCurve.AddKey(0.0f, 0.35f);
            sizeCurve.AddKey(0.35f, 1.0f);
            sizeCurve.AddKey(1.0f, 0.2f);
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1.0f, sizeCurve);

            // Point light illuminating staff tip and hands
            pointLight = indicatorObject.AddComponent<Light>();
            pointLight.type = LightType.Point;
            pointLight.range = 2.5f;
            pointLight.intensity = 1.0f;
            pointLight.shadows = LightShadows.None;

            // Active casting motes pool (small incandescent sphere + rim glow aura)
            for (int i = 0; i < MAX_POOLED_MOTES; i++)
            {
                GameObject moteObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(moteObj.GetComponent<Collider>());
                moteObj.name = "VHVR_MagicMote_" + i;
                moteObj.transform.SetParent(indicatorObject.transform, false);
                moteObj.transform.localScale = Vector3.one * 0.038f;
                moteObj.layer = LayerUtils.getWorldspaceUiLayer();

                var mRenderer = moteObj.GetComponent<MeshRenderer>();
                var mMat = unlitMatAsset != null ? Instantiate(unlitMatAsset) : new Material(Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Color"));
                mRenderer.material = mMat;
                mRenderer.receiveShadows = false;
                mRenderer.shadowCastingMode = ShadowCastingMode.Off;
                mRenderer.lightProbeUsage = LightProbeUsage.Off;
                mRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                GameObject auraObj = new GameObject("VHVR_MagicMoteAura_" + i);
                auraObj.transform.SetParent(moteObj.transform, false);
                auraObj.layer = LayerUtils.getWorldspaceUiLayer();

                MeshFilter auraMf = auraObj.AddComponent<MeshFilter>();
                auraMf.sharedMesh = GetTwoSidedQuadMesh();

                var aRenderer = auraObj.AddComponent<MeshRenderer>();
                var aMat = unlitMatAsset != null ? Instantiate(unlitMatAsset) : new Material(Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Color"));
                aMat.mainTexture = GetRimGlowTexture();
                aRenderer.material = aMat;
                aRenderer.receiveShadows = false;
                aRenderer.shadowCastingMode = ShadowCastingMode.Off;
                aRenderer.lightProbeUsage = LightProbeUsage.Off;
                aRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                moteObj.SetActive(false);

                motePool.Add(new MoteInstance
                {
                    gameObject = moteObj,
                    meshRenderer = mRenderer,
                    material = mMat,
                    auraObject = auraObj,
                    auraRenderer = aRenderer,
                    auraMaterial = aMat,
                    localOffset = Vector3.zero,
                    isActive = false
                });
            }

            indicatorObject.SetActive(false);

            // Reusable AudioSource added once
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 0.5f;
            audioSource.volume = 0.75f;
            audioSource.playOnAwake = false;
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }

            if (indicatorObject != null)
            {
                Destroy(indicatorObject);
                indicatorObject = null;
            }

            if (coreMaterial != null)
            {
                Destroy(coreMaterial);
                coreMaterial = null;
            }

            if (rimMaterial != null)
            {
                Destroy(rimMaterial);
                rimMaterial = null;
            }

            for (int i = 0; i < motePool.Count; i++)
            {
                var mote = motePool[i];
                if (mote.material != null)
                {
                    Destroy(mote.material);
                    mote.material = null;
                }
                if (mote.auraMaterial != null)
                {
                    Destroy(mote.auraMaterial);
                    mote.auraMaterial = null;
                }
                if (mote.gameObject != null)
                {
                    Destroy(mote.gameObject);
                    mote.gameObject = null;
                }
            }
            motePool.Clear();

            if (particleMaterial != null)
            {
                Destroy(particleMaterial);
                particleMaterial = null;
            }

            if (audioSource != null)
            {
                Destroy(audioSource);
                audioSource = null;
            }
        }

        private void OnDisable()
        {
            ResetCharge();
        }

        private void SpawnMoteWave(float activeDistance)
        {
            HideAllMotes();

            int count = Mathf.Clamp(VHVRConfig.MagicMotesPerStage(), 1, MAX_POOLED_MOTES);
            activeMotesRemaining = count;

            float minR = Mathf.Max(0.10f, VHVRConfig.MagicPrecastDistance() * 0.55f);
            float maxR = Mathf.Max(minR + 0.05f, activeDistance * 0.82f);

            for (int i = 0; i < count; i++)
            {
                var mote = motePool[i];
                Vector3 chosenPos = Vector3.zero;

                for (int attempt = 0; attempt < 12; attempt++)
                {
                    float r = UnityEngine.Random.Range(minR, maxR);
                    Vector3 dir = UnityEngine.Random.onUnitSphere;
                    if (dir.z < -0.3f)
                    {
                        dir.z = -dir.z;
                    }
                    dir.Normalize();
                    Vector3 testPos = dir * r;

                    bool separated = true;
                    for (int j = 0; j < i; j++)
                    {
                        if (Vector3.Distance(testPos, motePool[j].localOffset) < 0.09f)
                        {
                            separated = false;
                            break;
                        }
                    }

                    if (separated || attempt == 11)
                    {
                        chosenPos = testPos;
                        break;
                    }
                }

                mote.localOffset = chosenPos;
                mote.gameObject.transform.localPosition = chosenPos;
                mote.gameObject.SetActive(true);
                mote.isActive = true;
            }
        }

        private void HideAllMotes()
        {
            activeMotesRemaining = 0;
            for (int i = 0; i < motePool.Count; i++)
            {
                var mote = motePool[i];
                mote.isActive = false;
                if (mote.gameObject != null && mote.gameObject.activeSelf)
                {
                    mote.gameObject.SetActive(false);
                }
            }
        }

        private void PlayFizzle(Vector3 tipPos, VRInputSource secInputSource, VRHand secondaryHand)
        {
            PlaySound(isDissipate: true);

            if (secondaryHand?.hapticAction != null)
            {
                secondaryHand.hapticAction.Execute(0, 0.18f, 20f, 0.55f, secInputSource);
            }
            VRHand mainHand = VRPlayer.mainWeaponHand;
            if (mainHand?.hapticAction != null)
            {
                mainHand.hapticAction.Execute(0, 0.12f, 25f, 0.35f, VRPlayer.mainWeaponHandInputSource);
            }

            if (chargeParticles != null && indicatorObject != null)
            {
                indicatorObject.transform.position = tipPos;
                ParticleSystem.EmitParams ep = new ParticleSystem.EmitParams();
                ep.startColor = new Color(0.45f, 0.45f, 0.45f, 0.75f);
                ep.startSize = 0.025f;
                chargeParticles.Emit(ep, 16);
            }
        }

        private void PlayMoteAbsorbSound()
        {
            if (audioSource == null) return;
            if (!attemptedClipLoad)
            {
                LoadAudioClips();
            }
            if (cachedChimeClip != null)
            {
                float prevPitch = audioSource.pitch;
                audioSource.pitch = 1.35f;
                audioSource.PlayOneShot(cachedChimeClip, 0.45f);
                audioSource.pitch = prevPitch;
            }
            else
            {
                PlaySound(isDissipate: false);
            }
        }

        private void Update()
        {
            if (postCastCooldownTimer > 0f)
            {
                postCastCooldownTimer -= Time.deltaTime;
            }

            if (fizzleLockoutTimer > 0f)
            {
                fizzleLockoutTimer -= Time.deltaTime;
            }

            if (!VHVRConfig.GestureMagicCasting())
            {
                if (CurrentState != ChargeState.Idle)
                {
                    ResetCharge();
                }
                return;
            }

            if (Player.m_localPlayer == null || Player.m_localPlayer.IsDead())
            {
                if (CurrentState != ChargeState.Idle)
                {
                    ResetCharge();
                }
                return;
            }

            Vector3 tipPos = LocalWeaponWield.localWeaponTip;
            if (tipPos == Vector3.zero)
            {
                return;
            }

            VRHand secondaryHand = VRPlayer.isRightHandMainWeaponHand ? VRPlayer.leftHand : VRPlayer.rightHand;
            if (secondaryHand == null)
            {
                return;
            }

            Vector3 handPos = secondaryHand.transform.position;
            float dist = Vector3.Distance(handPos, tipPos);
            float entryThreshold = VHVRConfig.MagicPrecastDistance();
            float activeDistance = VHVRConfig.MagicActiveCastDistance();
            float requiredDuration = VHVRConfig.MagicPrecastDuration();
            bool activeMode = VHVRConfig.ActiveGestureCasting();
            Color magicColor = GetStaffMagicColor();

            VRInputSource secInputSource = VRPlayer.secondaryWeaponHandInputSource;

            switch (CurrentState)
            {
                case ChargeState.Idle:
                    if (indicatorObject.activeSelf)
                    {
                        indicatorObject.SetActive(false);
                    }

                    if (postCastCooldownTimer <= 0f && dist <= entryThreshold)
                    {
                        if (fizzleLockoutTimer > 0f)
                        {
                            return;
                        }

                        targetEitrCost = MagicStaffUtils.GetCurrentAttackEitrCost(Player.m_localPlayer);

                        if (activeMode && Player.m_localPlayer.GetEitr() < targetEitrCost)
                        {
                            fizzleLockoutTimer = VHVRConfig.MagicFizzleLockout();
                            PlayFizzle(tipPos, secInputSource, secondaryHand);
                            return;
                        }

                        CurrentState = ChargeState.Accumulating;
                        accumulatedTimer = 0f;
                        accumulatedEitr = 0f;
                        passiveFeedTimer = 0f;
                        graceRemaining = VHVRConfig.MagicGestureGracePeriod();
                        AccumulationProgress = 0f;
                        indicatorObject.SetActive(true);
                        if (chargeParticles != null && !chargeParticles.isPlaying)
                        {
                            chargeParticles.Play();
                        }

                        if (activeMode)
                        {
                            if (targetEitrCost <= 0.001f)
                            {
                                CurrentState = ChargeState.Charged;
                                AccumulationProgress = 1f;
                                chargeExpiryRemaining = VHVRConfig.MagicChargeExpiryDuration();
                                OnChargeComplete(tipPos);
                            }
                            else
                            {
                                SpawnMoteWave(activeDistance);
                                UpdateVfx(tipPos, 0.03f, 0.35f, magicColor, 15f);
                            }
                        }
                        else
                        {
                            HideAllMotes();
                            UpdateVfx(tipPos, 0.03f, 0.35f, magicColor, 15f);
                        }
                    }
                    break;

                case ChargeState.Accumulating:
                    if (activeMode)
                    {
                        if (dist <= activeDistance)
                        {
                            graceRemaining = VHVRConfig.MagicGestureGracePeriod();

                            // Passive feed
                            passiveFeedTimer += Time.deltaTime;
                            float feedInterval = Mathf.Max(0.05f, VHVRConfig.MagicPassiveFeedInterval());
                            while (passiveFeedTimer >= feedInterval)
                            {
                                passiveFeedTimer -= feedInterval;
                                accumulatedEitr += VHVRConfig.MagicPassiveFeedRate();
                                if (secondaryHand.hapticAction != null)
                                {
                                    secondaryHand.hapticAction.Execute(0, 0.02f, 40f, 0.12f, secInputSource);
                                }
                            }

                            // Mote collision check across active motes in the wave
                            if (activeMotesRemaining > 0)
                            {
                                float colRadius = VHVRConfig.MagicMoteCollisionRadius();
                                for (int i = 0; i < motePool.Count; i++)
                                {
                                    var mote = motePool[i];
                                    if (!mote.isActive || !mote.gameObject.activeSelf) continue;

                                    float distMote = Vector3.Distance(handPos, mote.gameObject.transform.position);
                                    if (distMote <= colRadius)
                                    {
                                        mote.isActive = false;
                                        mote.gameObject.SetActive(false);
                                        activeMotesRemaining--;

                                        accumulatedEitr += VHVRConfig.MagicMoteEitrBonus();

                                        if (secondaryHand.hapticAction != null)
                                        {
                                            secondaryHand.hapticAction.Execute(0, 0.045f, 90f, 0.65f, secInputSource);
                                        }
                                        PlayMoteAbsorbSound();

                                        if (chargeParticles != null)
                                        {
                                            ParticleSystem.EmitParams ep = new ParticleSystem.EmitParams();
                                            ep.position = mote.gameObject.transform.position;
                                            chargeParticles.Emit(ep, 10);
                                        }

                                        // If all motes in the stage are absorbed, spawn the next wave!
                                        if (activeMotesRemaining <= 0)
                                        {
                                            SpawnMoteWave(activeDistance);
                                        }

                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            graceRemaining -= Time.deltaTime;
                            if (graceRemaining <= 0f)
                            {
                                ResetCharge();
                                return;
                            }
                        }

                        AccumulationProgress = Mathf.Clamp01(accumulatedEitr / Mathf.Max(0.001f, targetEitrCost));
                        float coreScale = Mathf.Lerp(0.03f, 0.055f, AccumulationProgress);
                        float alpha = Mathf.Lerp(0.35f, 0.95f, AccumulationProgress);
                        float particleRate = Mathf.Lerp(15f, 40f, AccumulationProgress);
                        UpdateVfx(tipPos, coreScale, alpha, magicColor, particleRate);

                        if (accumulatedEitr >= targetEitrCost)
                        {
                            CurrentState = ChargeState.Charged;
                            AccumulationProgress = 1f;
                            chargeExpiryRemaining = VHVRConfig.MagicChargeExpiryDuration();
                            HideAllMotes();
                            OnChargeComplete(tipPos);
                        }
                    }
                    else
                    {
                        // Classic timer accumulation
                        if (dist <= entryThreshold)
                        {
                            accumulatedTimer += Time.deltaTime;
                            AccumulationProgress = Mathf.Clamp01(accumulatedTimer / requiredDuration);

                            if (Time.time - lastHapticTime >= HAPTIC_INTERVAL)
                            {
                                lastHapticTime = Time.time;
                                if (secondaryHand.hapticAction != null)
                                {
                                    secondaryHand.hapticAction.Execute(
                                        0,
                                        0.035f,
                                        Mathf.Lerp(25f, 85f, AccumulationProgress),
                                        Mathf.Lerp(0.12f, 0.5f, AccumulationProgress),
                                        secInputSource
                                    );
                                }
                            }

                            float coreScale = Mathf.Lerp(0.03f, 0.055f, AccumulationProgress);
                            float alpha = Mathf.Lerp(0.35f, 0.95f, AccumulationProgress);
                            float particleRate = Mathf.Lerp(15f, 40f, AccumulationProgress);
                            UpdateVfx(tipPos, coreScale, alpha, magicColor, particleRate);

                            if (accumulatedTimer >= requiredDuration)
                            {
                                CurrentState = ChargeState.Charged;
                                AccumulationProgress = 1f;
                                chargeExpiryRemaining = VHVRConfig.MagicChargeExpiryDuration();
                                OnChargeComplete(tipPos);
                            }
                        }
                        else
                        {
                            ResetCharge();
                        }
                    }
                    break;

                case ChargeState.Charged:
                    chargeExpiryRemaining -= Time.deltaTime;

                    if (dist <= entryThreshold)
                    {
                        chargeExpiryRemaining = VHVRConfig.MagicChargeExpiryDuration();
                    }

                    if (chargeExpiryRemaining <= 0f)
                    {
                        DissipateCharge();
                        return;
                    }

                    bool isExpiringSoon = chargeExpiryRemaining <= 2.0f;
                    float pulseSpeed = isExpiringSoon ? 18f : 5f;
                    float corePulse = 0.052f + (isExpiringSoon ? 0.008f : 0.004f) * Mathf.Sin(Time.time * pulseSpeed);
                    float chargedAlpha = isExpiringSoon ? (0.6f + 0.35f * Mathf.Sin(Time.time * 24f)) : 0.95f;

                    UpdateVfx(tipPos, corePulse, chargedAlpha, magicColor, isExpiringSoon ? 50f : 35f);
                    break;

                case ChargeState.Channeling:
                    float channelPulse = 0.05f + 0.008f * Mathf.Sin(Time.time * 20f);
                    UpdateVfx(tipPos, channelPulse, 0.95f, magicColor, 50f);

                    bool triggerHeld = MagicStaffUtils.IsShootingTriggerHeld();
                    if (!triggerHeld)
                    {
                        ResetCharge();
                        postCastCooldownTimer = 0.2f;
                    }
                    break;
            }
        }

        private void LateUpdate()
        {
            if (indicatorObject != null && indicatorObject.activeSelf)
            {
                Vector3 tipPos = LocalWeaponWield.localWeaponTip;
                if (tipPos != Vector3.zero)
                {
                    indicatorObject.transform.position = tipPos;
                    if (LocalWeaponWield.weaponForward != Vector3.zero)
                    {
                        indicatorObject.transform.rotation = Quaternion.LookRotation(LocalWeaponWield.weaponForward, Vector3.up);
                    }
                }

                Camera mainCam = Utils.GetMainCamera();
                if (mainCam != null)
                {
                    if (rimObject != null)
                    {
                        Vector3 toCam = rimObject.transform.position - mainCam.transform.position;
                        if (toCam.sqrMagnitude > 0.0001f)
                        {
                            rimObject.transform.rotation = Quaternion.LookRotation(toCam);
                        }
                    }

                    for (int i = 0; i < motePool.Count; i++)
                    {
                        var mote = motePool[i];
                        if (mote.isActive && mote.gameObject != null && mote.gameObject.activeSelf)
                        {
                            if (mote.auraObject != null)
                            {
                                Vector3 toCamMote = mote.auraObject.transform.position - mainCam.transform.position;
                                if (toCamMote.sqrMagnitude > 0.0001f)
                                {
                                    mote.auraObject.transform.rotation = Quaternion.LookRotation(toCamMote);
                                }
                            }

                            float motePulse = 0.038f + 0.006f * Mathf.Sin(Time.time * 16f + i * 1.5f);
                            mote.gameObject.transform.localScale = Vector3.one * motePulse;
                            if (mote.auraObject != null)
                            {
                                mote.auraObject.transform.localScale = Vector3.one * (motePulse * 1.8f);
                            }
                        }
                    }
                }
            }
        }

        private void UpdateVfx(Vector3 position, float coreScale, float alpha, Color baseColor, float particleRate)
        {
            if (indicatorObject == null) return;

            if (!indicatorObject.activeSelf)
            {
                indicatorObject.SetActive(true);
            }

            indicatorObject.transform.position = position;

            // 1. Translucent inner 3D sphere: provides stereoscopic volume and depth
            if (coreRenderer != null)
            {
                coreRenderer.transform.localScale = Vector3.one * coreScale;
            }

            if (coreMaterial != null)
            {
                coreMaterial.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha * 0.18f);
            }

            // 2. Outer rim aura billboard: curved Fresnel-like rim glow (outer edges more opaque than inner)
            if (rimRenderer != null)
            {
                rimRenderer.transform.localScale = Vector3.one * (coreScale * 1.55f);
            }

            if (rimMaterial != null)
            {
                rimMaterial.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha * 0.85f);
            }

            // 3. Active casting motes
            for (int i = 0; i < motePool.Count; i++)
            {
                var mote = motePool[i];
                if (mote.material != null)
                {
                    mote.material.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.95f);
                }
                if (mote.auraMaterial != null)
                {
                    mote.auraMaterial.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.85f);
                }
            }

            // 4. Swirling orbital particles: soft, translucent glowing motes
            if (chargeParticles != null)
            {
                var main = chargeParticles.main;
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(baseColor.r, baseColor.g, baseColor.b, alpha * 0.60f));

                var emission = chargeParticles.emission;
                emission.rateOverTime = particleRate;
            }

            // 5. Dynamic element point light
            if (pointLight != null)
            {
                pointLight.color = baseColor;
                pointLight.intensity = alpha * 1.6f;
            }
        }

        private Color GetStaffMagicColor()
        {
            var item = Player.m_localPlayer?.GetRightItem();
            if (item?.m_shared?.m_name != null)
            {
                string name = item.m_shared.m_name.ToLowerInvariant();
                if (name.Contains("fire") || name.Contains("cluster") || name.Contains("ember"))
                {
                    return new Color(1f, 0.45f, 0.1f); // Fire Orange
                }
                if (name.Contains("frost") || name.Contains("ice"))
                {
                    return new Color(0.2f, 0.85f, 1f); // Frost Cyan
                }
                if (name.Contains("green") || name.Contains("root") || name.Contains("stem"))
                {
                    return new Color(0.25f, 1f, 0.45f); // Nature Green
                }
                if (name.Contains("troll") || name.Contains("blood") || name.Contains("skeleton"))
                {
                    return new Color(1f, 0.15f, 0.25f); // Blood Red
                }
                if (name.Contains("thunder") || name.Contains("lightning") || name.Contains("dundr"))
                {
                    return new Color(1f, 0.9f, 0.25f); // Lightning Gold
                }
            }
            return new Color(0.35f, 0.8f, 1f); // Arcane Blue
        }

        private void OnChargeComplete(Vector3 tipPos)
        {
            VRHand secondaryHand = VRPlayer.isRightHandMainWeaponHand ? VRPlayer.leftHand : VRPlayer.rightHand;
            VRHand mainHand = VRPlayer.mainWeaponHand;

            if (secondaryHand?.hapticAction != null)
            {
                secondaryHand.hapticAction.Execute(0, 0.12f, 95f, 0.75f, VRPlayer.secondaryWeaponHandInputSource);
            }
            if (mainHand?.hapticAction != null)
            {
                mainHand.hapticAction.Execute(0, 0.10f, 85f, 0.45f, VRPlayer.mainWeaponHandInputSource);
            }

            // Radial spark burst when full charge is reached
            if (chargeParticles != null)
            {
                chargeParticles.Emit(25);
            }

            PlaySound(isDissipate: false);
        }

        private void DissipateCharge()
        {
            ResetCharge();
            postCastCooldownTimer = 0.3f;

            PlaySound(isDissipate: true);

            VRHand mainHand = VRPlayer.mainWeaponHand;
            if (mainHand?.hapticAction != null)
            {
                mainHand.hapticAction.Execute(0, 0.12f, 35f, 0.35f, VRPlayer.mainWeaponHandInputSource);
            }
        }

        public bool OnAttemptAttack(Attack attack)
        {
            if (!VHVRConfig.GestureMagicCasting())
            {
                return true;
            }

            if (CurrentState == ChargeState.Charged)
            {
                bool isLooping = attack != null && attack.m_loopingAttack;
                if (isLooping)
                {
                    CurrentState = ChargeState.Channeling;
                }
                else
                {
                    // Discrete spell: consume charge and set brief cooldown
                    ResetCharge();
                    postCastCooldownTimer = 0.25f;
                }
                return true;
            }

            if (CurrentState == ChargeState.Channeling)
            {
                return true;
            }

            return false;
        }

        public void ResetCharge()
        {
            CurrentState = ChargeState.Idle;
            accumulatedTimer = 0f;
            accumulatedEitr = 0f;
            targetEitrCost = 0f;
            passiveFeedTimer = 0f;
            graceRemaining = 0f;
            chargeExpiryRemaining = 0f;
            AccumulationProgress = 0f;

            HideAllMotes();

            if (chargeParticles != null && chargeParticles.isPlaying)
            {
                chargeParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }

            if (indicatorObject != null && indicatorObject.activeSelf)
            {
                indicatorObject.SetActive(false);
            }
        }

        private void LoadAudioClips()
        {
            if (attemptedClipLoad) return;
            attemptedClipLoad = true;

            var chimePrefab = ZNetScene.instance?.GetPrefab("sfx_StaffLightning_charge")
                           ?? ZNetScene.instance?.GetPrefab("sfx_icestaff_start");
            if (chimePrefab != null)
            {
                var src = chimePrefab.GetComponentInChildren<AudioSource>();
                if (src != null)
                {
                    cachedChimeClip = src.clip;
                }
            }

            var dissipatePrefab = ZNetScene.instance?.GetPrefab("sfx_icestaff_start")
                               ?? ZNetScene.instance?.GetPrefab("sfx_shield_start_frost");
            if (dissipatePrefab != null)
            {
                var src = dissipatePrefab.GetComponentInChildren<AudioSource>();
                if (src != null)
                {
                    cachedDissipateClip = src.clip;
                }
            }
        }

        private void PlaySound(bool isDissipate)
        {
            if (audioSource == null) return;

            if (!attemptedClipLoad)
            {
                LoadAudioClips();
            }

            audioSource.pitch = 1.0f;
            AudioClip clipToPlay = isDissipate ? cachedDissipateClip : cachedChimeClip;
            if (clipToPlay != null)
            {
                audioSource.PlayOneShot(clipToPlay, isDissipate ? 0.45f : 0.75f);
            }
        }
    }
}
