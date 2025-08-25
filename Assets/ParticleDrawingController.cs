using UnityEngine;
using UnityEngine.Rendering;
using System.IO;

/// <summary>
/// 既存の実装を拡張：
/// - 低解像度 velocity RT を計算して CPU に Readback → 粒子の移動に使用
/// - rimHeightTexture による縁の扱い（シェーダ側で clamp を行う想定）
/// - ジャイロ or PC 用入力で panTransform を回転（傾き）
/// 使い方：
/// - Inspector に heightMaterial / normalMaterial / targetMaterial / velocityMaterial / rimHeightTexture / panTransform を割当てる
/// - velocityMaterial は前に示した VelocityCalc.shader または同等のインターフェースを持つこと
/// </summary>
public class ParticleDrawingController : MonoBehaviour
{
    [Header("パーティクル設定")]
    [SerializeField] private int particleCount = 10000;
    [SerializeField] private float particleSize = 0.05f;

    [Header("リソース")]
    [SerializeField] private Material heightMaterial;
    [SerializeField] private Material normalMaterial;
    [SerializeField] private Material targetMaterial;
    [SerializeField] private Texture2D particleTexture;

    [Header("レンダーテクスチャ設定")]
    [SerializeField] private int textureResolution = 512;

    [Header("Velocity (低解像度)")]
    [SerializeField] private Material velocityMaterial; // VelocityCalc.shader を割当て
    [SerializeField] private Texture2D rimHeightTexture; // R チャンネルに rim height を格納
    [SerializeField] private int velocityResolution = 128; // 低解像度 RT（CPU Readback 用）
    [SerializeField] private float velocitySampleInterval = 0.05f; // Readback インターバル（秒）
    [SerializeField] private float velocityToParticleScale = 1.0f; // velocity -> particle 移動倍率
    [SerializeField] private float velocitySmoothing = 0.15f; // 0..1（小さいほど応答速い、0.15くらいが滑らか）
    [SerializeField] private float particleFriction = 1.5f;   // 秒あたりの摩擦（1〜3くらい）
    [SerializeField] private float maxParticleSpeed = 0.5f;    // 粒子が出せる上限速度（画面単位/sec）
    [SerializeField] private float gravityScale = 0.5f;       // shader に渡す前に重力を抑える
    [SerializeField] private bool showVelocityDebug = true;   // 画面に RT を小さく表示

    [Header("入力 / 傾き")]
    [SerializeField] private bool useGyroOnDevice = true; // モバイルでジャイロを使うか
    [SerializeField] private float pcTiltSpeed = 40f; // WASD/矢印で傾ける速度（deg/sec）
    [SerializeField] private float mouseTiltSensitivity = 0.2f; // 右ドラッグでの感度

    // デバッグ切替（ファイル保存や Readback は高コストなので任意で有効化）
    [Header("デバッグ")]
    [SerializeField] private bool enableDebugReadback = true;
    [SerializeField] private float savePngInterval = 1.0f; // PNG 保存間隔(秒)

    // --- 内部状態 ---
    private RenderTexture rtHeight;
    private RenderTexture rtNormal;
    private RenderTexture rtVelocity; // 低解像度の速度RT
    private Mesh quadMesh;
    private Vector3[] particlePositions;
    private Matrix4x4[] matrices;
    private CommandBuffer commandBuffer;
    private Quaternion panLocalRotation = Quaternion.identity;
    private Vector2[] particleVelocities; // 粒子ごとの速度を保持（平滑化・摩擦に使用）
    private Quaternion prevPanLocalRotation = Quaternion.identity;

    // velocity readback
    private Texture2D velocityReadTex;
    private Color[] velocityPixels;
    private float lastVelocityReadTime = 0f;

    // デバッグ用のタイマーと読み出し用テクスチャ（再利用）
    private float lastSaveTime = 0f;
    private Texture2D readbackTex = null;

    // PC 傾き管理（角度で管理）
    private float tiltX = 0f; // around Z or X depending mapping
    private float tiltY = 0f;
    private bool isRightMouseDragging = false;
    private Vector3 lastMousePos = Vector3.zero;

    void Start()
    {
        // ---------- RenderTexture の初期化 ----------
        rtHeight = new RenderTexture(textureResolution, textureResolution, 0, RenderTextureFormat.ARGBHalf)
        {
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        rtHeight.Create();

        rtNormal = new RenderTexture(textureResolution, textureResolution, 0, RenderTextureFormat.ARGBHalf)
        {
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        rtNormal.Create();

        // velocity RT (低解像度)
        rtVelocity = new RenderTexture(velocityResolution, velocityResolution, 0, RenderTextureFormat.ARGBHalf)
        {
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        rtVelocity.Create();

        // targetMaterial (シーン内で高さ/法線を参照するマテリアル) に RT をセット
        if (targetMaterial != null)
        {
            targetMaterial.SetTexture("_HeightMap", rtHeight);
            targetMaterial.SetTexture("_NormalMap", rtNormal);
        }

        // heightMaterial のメインテクスチャとインスタンシング有効化
        if (heightMaterial != null && particleTexture != null)
        {
            heightMaterial.SetTexture("_MainTex", particleTexture);
            heightMaterial.enableInstancing = true;
        }

        // ---------- パーティクル配列の初期化 ----------
        particlePositions = new Vector3[particleCount];
        matrices = new Matrix4x4[particleCount];
        for (int i = 0; i < particleCount; i++)
        {
            // 初期位置は -0.1..0.1（スクリーン/パン空間）。レンダリングの Ortho が -0.5..0.5.
            particlePositions[i] = new Vector3(Random.Range(-0.1f, 0.1f), Random.Range(-0.1f, 0.1f), 0);
            matrices[i] = Matrix4x4.identity;
        }

        // ---------- クワッド Mesh の作成 ----------
        quadMesh = new Mesh();
        quadMesh.vertices = new Vector3[] {
            new Vector3(-0.5f, -0.5f, 0),
            new Vector3( 0.5f, -0.5f, 0),
            new Vector3(-0.5f,  0.5f, 0),
            new Vector3( 0.5f,  0.5f, 0),
        };
        quadMesh.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        quadMesh.triangles = new int[] {
            0,2,1, 2,3,1, // 表
            1,2,0, 1,3,2  // 裏（元実装に合わせて二重書き）
        };
        quadMesh.RecalculateBounds();

        // CommandBuffer を用意
        commandBuffer = new CommandBuffer { name = "Particle Drawing Debug" };

        // normalMaterial に事前に TexelSize/Strength をセット（Blit 時に使う）
        if (normalMaterial != null)
        {
            normalMaterial.SetFloat("_Strength", 1.0f);
            normalMaterial.SetVector("_TexelSize", new Vector4(1.0f / textureResolution, 1.0f / textureResolution, 0, 0));
        }

        // velocityMaterial の初期値セット
        if (velocityMaterial != null)
        {
            velocityMaterial.SetVector("_TexelSize", new Vector4(1.0f / velocityResolution, 1.0f / velocityResolution, 0, 0));
            if (rimHeightTexture != null) velocityMaterial.SetTexture("_RimHeight", rimHeightTexture);
            // 初期チューニング値（Inspector で変更可能）
            velocityMaterial.SetFloat("_kSlope", 2.0f);
            velocityMaterial.SetFloat("_kGravity", 5.0f);
            velocityMaterial.SetFloat("_maxVel", 2.0f);
        }

        // readback 用
        velocityReadTex = new Texture2D(velocityResolution, velocityResolution, TextureFormat.RGBAFloat, false);
        velocityPixels = new Color[velocityResolution * velocityResolution];

        // デバッグ用読み出し Texture2D を必要時に作成して再利用（毎フレームの GC を防ぐ）
        if (enableDebugReadback)
        {
            readbackTex = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
        }

        // ジャイロ初期化（端末で有効）
        if (useGyroOnDevice && SystemInfo.supportsGyroscope)
        {
            Input.gyro.enabled = true;
        }

        // Start() の終わり近くに追加
        particleVelocities = new Vector2[particleCount];
        for (int i = 0; i < particleCount; i++) particleVelocities[i] = Vector2.zero;
        prevPanLocalRotation = panLocalRotation;

        // Velocity shader のより安全な初期値
        if (velocityMaterial != null)
        {
            velocityMaterial.SetFloat("_kSlope", 0.6f);   // 小さめ
            velocityMaterial.SetFloat("_kGravity", 1.0f); // 小さめ
            velocityMaterial.SetFloat("_maxVel", 0.6f);   // 安全側
        }
        velocityToParticleScale = Mathf.Clamp(velocityToParticleScale, 0.05f, 2.0f); // 安全域

        Debug.Log("[DEBUG] ParticleDrawingController_Debug_Modified started. RT sizes: " + textureResolution + "x" + textureResolution + ", velocity: " + velocityResolution + "x" + velocityResolution);
    }

    void Update()
    {
        // 0) 入力で pan の傾きを更新（スマホ: ジャイロ、PC: WASD / マウス右ドラッグ）
        UpdatePanTiltInput();

        // 1) パーティクル位置更新（CPU側）：velocity テクスチャの最新データを使って移動
        UpdateParticlePositions();

        // 2) コマンドバッファに描画命令を積む（高さ RT にパーティクルを書き込む）
        DrawParticlesToHeightRT();

        // 3) 高さ RT を元に法線 RT を生成（Blit）
        if (normalMaterial != null)
        {
            Graphics.Blit(rtHeight, rtNormal, normalMaterial);
        }

        // 4) velocity RT を GPU で計算（height + normal を参照）→ 低解像度 RT に出力
        if (velocityMaterial != null)
        {
            // Compute gravity in pan-local coordinates (panLocalRotation is internal, object not rotated)
Vector3 gravityWorld = Physics.gravity; // (0, -9.81f, 0)
Vector3 gravityInPanLocal = Quaternion.Inverse(panLocalRotation) * gravityWorld;

// Dead zone: 小さな傾きは無視
float tiltAngle = Quaternion.Angle(panLocalRotation, Quaternion.identity);
if (tiltAngle < 0.5f) gravityInPanLocal = Vector3.zero;

// MAP: テクスチャの V 軸 = pan-local Z（画面奥が下）の想定
// つまり shader 側の (Gx, Gy) には (pan.x, pan.z) を渡す
float gscale = gravityScale; // Inspector で設定している scale
Vector3 gravityForShader = new Vector3(gravityInPanLocal.x * gscale, gravityInPanLocal.z * gscale, 0f);

// デバッグログ（任意）
if (Time.frameCount % 60 == 0) Debug.Log($"[GRAVITY MAP] panLocal:{gravityInPanLocal} -> shader:{gravityForShader} tilt:{tiltAngle:F2}");

// これを velocityMaterial に渡す（shader 側は _Gravity.x=_Gx, _Gravity.y=_Gy を想定）
velocityMaterial.SetVector("_Gravity", new Vector4(gravityForShader.x, gravityForShader.y, gravityForShader.z, 0f));
            velocityMaterial.SetTexture("_HeightMap", rtHeight);
            velocityMaterial.SetTexture("_NormalMap", rtNormal);
            if (rimHeightTexture != null) velocityMaterial.SetTexture("_RimHeight", rimHeightTexture);

            Graphics.Blit(null, rtVelocity, velocityMaterial);
        }

        // 5) 低解像度 velocity を CPU に読み出し（間引き、重いなら interval を長く）
        if (Time.time - lastVelocityReadTime >= velocitySampleInterval)
        {
            ReadbackVelocityRTToCPU();
            lastVelocityReadTime = Time.time;
        }

        // 6) デバッグ読み出し・保存（有効時のみ・インターバルで制御）
        if (enableDebugReadback && Time.time - lastSaveTime >= savePngInterval)
        {
            Color centerH = ReadPixelFromRT(rtHeight, textureResolution / 2, textureResolution / 2);
            Color centerN = ReadPixelFromRT(rtNormal, textureResolution / 2, textureResolution / 2);
            Debug.Log($"[DEBUG READBACK] rtHeight center: {centerH} | rtNormal center: {centerN}");

            SaveRTToPNG(rtHeight, "rtHeight_after_cmdloop.png");
            SaveRTToPNG(rtNormal, "rtNormal_after_blit.png");

            lastSaveTime = Time.time;
        }
    }

    // -------------------------
    // Drawing
    // -------------------------
    private void DrawParticlesToHeightRT()
    {
        commandBuffer.Clear();
        commandBuffer.SetRenderTarget(rtHeight);
        commandBuffer.SetViewport(new Rect(0, 0, textureResolution, textureResolution));
        commandBuffer.ClearRenderTarget(false, true, Color.black);

        Matrix4x4 proj = Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, -1f, 1f);
        Matrix4x4 view = Matrix4x4.TRS(new Vector3(0, 0, -0.5f), Quaternion.identity, Vector3.one).inverse;
        commandBuffer.SetViewProjectionMatrices(view, proj);

        if (heightMaterial == null)
        {
            // safety
            Graphics.ExecuteCommandBuffer(commandBuffer);
            return;
        }

        // Draw particles - this is heavy for large particleCount; consider GPU instancing / DrawMeshInstanced
        for (int i = 0; i < particleCount; i++)
        {
            commandBuffer.DrawMesh(quadMesh, matrices[i], heightMaterial, 0, 0);
        }

        Graphics.ExecuteCommandBuffer(commandBuffer);
    }

    // -------------------------
    // Particle update (CPU)
    // -------------------------
    void UpdateParticlePositions()
    {
        float dt = Time.deltaTime;
        for (int i = 0; i < particleCount; i++)
        {
            Vector3 p = particlePositions[i];

            // uv mapping
            float u = Mathf.Clamp01((p.x + 0.5f));
            float v = Mathf.Clamp01((p.y + 0.5f));

            // サンプル（velocityPixels が無ければゼロベクトルを扱う）
            Vector2 sampled = Vector2.zero;
            if (velocityPixels != null && velocityPixels.Length == velocityResolution * velocityResolution)
            {
                sampled = SampleVelocityFromCPU(u, v);
            }

            // 平滑化（前の値に lerp して急変を抑える）
            particleVelocities[i] = Vector2.Lerp(particleVelocities[i], sampled, velocitySmoothing);

            // 摩擦（毎秒減衰）
            float frictionFactor = Mathf.Clamp01(1f - particleFriction * dt);
            particleVelocities[i] *= frictionFactor;

            // 速度上限（安全のため）
            if (particleVelocities[i].magnitude > maxParticleSpeed)
                particleVelocities[i] = particleVelocities[i].normalized * maxParticleSpeed;

            // 座標更新（スケールと dt を適用）
            p += new Vector3(particleVelocities[i].x, particleVelocities[i].y, 0f) * velocityToParticleScale * dt;

            // bounds
            p.x = Mathf.Clamp(p.x, -0.5f, 0.5f);
            p.y = Mathf.Clamp(p.y, -0.5f, 0.5f);

            particlePositions[i] = p;
            matrices[i] = Matrix4x4.TRS(p, Quaternion.identity, new Vector3(particleSize, particleSize, particleSize));
        }
    }


    // -------------------------
    // CPU sampling from velocityPixels (bilinear)
    // -------------------------
    private Vector2 SampleVelocityFromCPU(float u, float v)
    {
        if (velocityPixels == null || velocityPixels.Length == 0) return Vector2.zero;

        u = Mathf.Clamp01(u);
        v = Mathf.Clamp01(v);
        float fx = u * (velocityResolution - 1);
        float fy = v * (velocityResolution - 1);
        int x0 = Mathf.FloorToInt(fx);
        int y0 = Mathf.FloorToInt(fy);
        int x1 = Mathf.Min(x0 + 1, velocityResolution - 1);
        int y1 = Mathf.Min(y0 + 1, velocityResolution - 1);
        float sx = fx - x0;
        float sy = fy - y0;

        Color c00 = velocityPixels[y0 * velocityResolution + x0];
        Color c10 = velocityPixels[y0 * velocityResolution + x1];
        Color c01 = velocityPixels[y1 * velocityResolution + x0];
        Color c11 = velocityPixels[y1 * velocityResolution + x1];

        Color c0 = Color.Lerp(c00, c10, sx);
        Color c1 = Color.Lerp(c01, c11, sx);
        Color c = Color.Lerp(c0, c1, sy);

        return new Vector2(c.r, c.g); // RG に vx, vy を格納する想定
    }

    // -------------------------
    // Velocity Readback
    // -------------------------
    private void ReadbackVelocityRTToCPU()
    {
        if (rtVelocity == null || velocityReadTex == null) return;

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rtVelocity;

        velocityReadTex.ReadPixels(new Rect(0, 0, velocityResolution, velocityResolution), 0, 0);
        velocityReadTex.Apply();
        // GetPixels は便利だが GC を生む。必要なら GetRawTextureData + NativeArray に置換
        velocityPixels = velocityReadTex.GetPixels();

        RenderTexture.active = prev;

        // debug: min/max/avg
        float maxV = 0f, sum = 0f;
        int cnt = velocityPixels.Length;
        for (int i = 0; i < cnt; i++)
        {
            Vector2 vv = new Vector2(velocityPixels[i].r, velocityPixels[i].g);
            float len = vv.magnitude;
            sum += len;
            if (len > maxV) maxV = len;
        }
        float avg = sum / Mathf.Max(1, cnt);
        if (maxV > 0.001f)
            Debug.Log($"[VEL DEBUG] max:{maxV:F4}, avg:{avg:F4}");
    }

    // -------------------------
    // Input & pan tilt (gyro or pc controls)
    // -------------------------
    private void UpdatePanTiltInput()
    {
        bool usingGyro = useGyroOnDevice && SystemInfo.supportsGyroscope && !Application.isEditor;

        if (usingGyro)
        {
            Quaternion g = Input.gyro.attitude;
            Quaternion gyroUnity = new Quaternion(-g.x, -g.y, g.z, g.w);

            // デッドゾーン（微小回転は identity に置き換え）
            Vector3 e = gyroUnity.eulerAngles;
            // convert to -180..180
            e.x = (e.x > 180f) ? e.x - 360f : e.x;
            e.y = (e.y > 180f) ? e.y - 360f : e.y;
            e.z = (e.z > 180f) ? e.z - 360f : e.z;

            float deadAngle = 0.7f; // degrees
            if (Mathf.Abs(e.x) < deadAngle && Mathf.Abs(e.z) < deadAngle)
            {
                gyroUnity = Quaternion.identity;
            }

            // スムージング（前回値と slerp）
            panLocalRotation = Quaternion.Slerp(prevPanLocalRotation, gyroUnity, 0.08f);
            prevPanLocalRotation = panLocalRotation;
        }
        else
        {
            // PC: キーと右ドラッグで tiltX/tiltY を更新するが、panTransform を回転させない
            float dt = Time.deltaTime;
            float kx = Input.GetAxis("Horizontal"); // A/D or Left/Right
            float ky = Input.GetAxis("Vertical");   // W/S or Up/Down
            tiltX += -kx * pcTiltSpeed * dt;
            tiltY += ky * pcTiltSpeed * dt;

            if (Input.GetMouseButtonDown(1))
            {
                isRightMouseDragging = true;
                lastMousePos = Input.mousePosition;
            }
            else if (Input.GetMouseButtonUp(1))
            {
                isRightMouseDragging = false;
            }

            if (isRightMouseDragging)
            {
                Vector3 delta = Input.mousePosition - lastMousePos;
                tiltX += delta.x * mouseTiltSensitivity;
                tiltY += delta.y * mouseTiltSensitivity;
                lastMousePos = Input.mousePosition;
            }

            // clamp
            tiltX = Mathf.Clamp(tiltX, -45f, 45f);
            tiltY = Mathf.Clamp(tiltY, -45f, 45f);

            // 内部回転を作る（見た目は変えない）
            panLocalRotation = Quaternion.Euler(tiltY, 0f, -tiltX);
        }
    }


    // -------------------------
    // RT Read/Save helpers (unchanged)
    // -------------------------
    private Color ReadPixelFromRT(RenderTexture rt, int x, int y)
    {
        if (readbackTex == null)
        {
            readbackTex = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
        }

        RenderTexture old = RenderTexture.active;
        RenderTexture.active = rt;

        readbackTex.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
        readbackTex.Apply();
        Color c = readbackTex.GetPixel(0, 0);

        RenderTexture.active = old;
        return c;
    }

    private void SaveRTToPNG(RenderTexture rt, string fileName)
    {
        try
        {
            RenderTexture old = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();

            byte[] bytes = tex.EncodeToPNG();
            string path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllBytes(path, bytes);
            Debug.Log("[DEBUG SAVE] Saved " + path);

            Destroy(tex);
            RenderTexture.active = old;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[DEBUG SAVE] failed to save PNG: " + e);
        }
    }

    private void OnDestroy()
    {
        if (rtHeight != null) rtHeight.Release();
        if (rtNormal != null) rtNormal.Release();
        if (rtVelocity != null) rtVelocity.Release();
        if (commandBuffer != null) commandBuffer.Release();
        if (readbackTex != null) Destroy(readbackTex);
        if (velocityReadTex != null) Destroy(velocityReadTex);
    }

    void OnGUI()
    {
        if (!showVelocityDebug || rtVelocity == null) return;
        GUI.DrawTexture(new Rect(10, 10, 128, 128), rtVelocity, ScaleMode.ScaleToFit, false);
    }
}
