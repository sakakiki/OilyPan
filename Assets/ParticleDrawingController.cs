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
    [SerializeField] private float velocitySampleInterval = 0.03f; // Readback インターバル（秒）- 応答性向上
    [SerializeField] private float velocityToParticleScale = 3.0f; // velocity -> particle 移動倍率（増加）
    [SerializeField] private float velocitySmoothing = 0.25f; // 0..1（応答性向上のため若干増加）
    [SerializeField] private float particleFriction = 0.8f;   // 摩擦を減らして流れやすく
    [SerializeField] private float maxParticleSpeed = 2.0f;    // 粒子の上限速度を大幅に増加
    [SerializeField] private float gravityScale = 1.5f;       // 重力の影響を増加
    [SerializeField] private bool showVelocityDebug = true;   // 画面に RT を小さく表示

    [Header("入力 / 傾き")]
    [SerializeField] private bool useGyroOnDevice = true; // モバイルでジャイロを使うか
    [SerializeField] private float pcTiltMax = 45f; // WASD・矢印で傾けることのできる最大角度

    // デバッグ切替（ファイル保存や Readback は高コストなので任意で有効化）
    [Header("デバッグ")]
    [SerializeField] private bool enableDebugReadback = true;
    [SerializeField] private float savePngInterval = 1.0f; // PNG 保存間隔(秒)// 計算用 rtVelocity は既にある。
    // 可視化用 RT を追加
    private RenderTexture rtVelocityDebug;
    [SerializeField] private Material velocityVisualizerMaterial; // assign VelocityCalc_Visualizer
    [SerializeField] private int debugDisplaySize = 128; // 表示サイズ（小さくてOK）

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
            particlePositions[i] = new Vector3(Random.Range(-0.01f, 0.01f), Random.Range(-0.01f, 0.01f), 0);
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

        // Velocity shader のより安全な初期値
        if (velocityMaterial != null)
        {
            velocityMaterial.SetFloat("_kSlope", 1.2f);   // 勾配感度を増加
            velocityMaterial.SetFloat("_kGravity", 2.0f); // 重力寄与を増加
            velocityMaterial.SetFloat("_maxVel", 2.0f);   // シェーダー側上限も増加
        }
        velocityToParticleScale = Mathf.Clamp(velocityToParticleScale, 0.05f, 5.0f); // 上限を拡張

        rtVelocityDebug = new RenderTexture(debugDisplaySize, debugDisplaySize, 0, RenderTextureFormat.ARGBHalf) {
            useMipMap = false, autoGenerateMips = false, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
        };
        rtVelocityDebug.Create();


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
            // 重力をパンのローカル座標系に変換
            Vector3 gravityWorld = Physics.gravity; // (0,-9.81,0)
            Vector3 gravityInPanLocal = Quaternion.Inverse(panLocalRotation) * gravityWorld;

            // パンの座標系からテクスチャ座標系へのマッピング
            // パンX軸（左右傾き） → テクスチャU方向、パンZ軸（前後傾き） → テクスチャV方向
            float rawGx = gravityInPanLocal.x;  // テクスチャU方向（左右）の重力成分
            float rawGz = gravityInPanLocal.z;  // テクスチャV方向（前後）の重力成分
            
            // デバッグ用：重力成分をログ出力
            if (Time.frameCount % 30 == 0) // 30フレームに1回ログ出力（パフォーマンス考慮）
            {
                Debug.Log($"Gravity local: ({gravityInPanLocal.x:F3}, {gravityInPanLocal.y:F3}, {gravityInPanLocal.z:F3}) | Raw shader: ({rawGx:F3}, {rawGz:F3})");
            }

            // scale down heavily for debug so we see effect without blow-up
            float debugScale = Mathf.Min(gravityScale, 0.5f); // force <= 0.5 for now
            Vector3 gravityForShader = new Vector3(rawGx * debugScale, rawGz * debugScale, 0f);

            // clamp magnitude to avoid huge numbers
            float mag = gravityForShader.magnitude;
            if (mag > 2.0f) gravityForShader = gravityForShader.normalized * 2.0f;
/*
// --- debug injection before Graphics.Blit(null, rtVelocity, velocityMaterial);
velocityMaterial.SetFloat("_DebugMode", 1f); // 1=show G, 2=show g_tangent, 3=show mag, 0=normal
velocityMaterial.SetFloat("_DebugScale", 0.1f); // 見やすい値に調整
// also reduce gains to make visible and stable
velocityMaterial.SetFloat("_kSlope", 0.0f);   // turn off slope to isolate gravity
velocityMaterial.SetFloat("_kGravity", 1.0f);
velocityMaterial.SetFloat("_maxVel", 2.0f);
*/

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
            // ジャイロ座標系からUnity座標系への変換を修正
            // ジャイロ: x=pitch, y=yaw, z=roll → Unity: x=pitch, y=yaw, z=roll
            Quaternion gyroUnity = new Quaternion(g.x, g.y, -g.z, g.w);
            
            // デバイス向きを考慮した回転補正（縦持ち想定）
            // 90度回転してデバイスのピッチ/ロールをパンのX/Z軸に適切にマップ
            Quaternion deviceOrientation = Quaternion.Euler(90, 0, 0);
            panLocalRotation = deviceOrientation * gyroUnity;
        }
        else
        {
            // PC: キーで tiltX/tiltY を更新する
            float kx = Input.GetAxis("Horizontal"); // A/D or Left/Right
            float ky = Input.GetAxis("Vertical");   // W/S or Up/Down
            tiltX = kx * pcTiltMax;
            tiltY = ky * pcTiltMax;

            // パンの回転を作る（テクスチャ座標系と一致、A/D方向を反転）
            // A/D（左右入力）→ Z軸回転（ロール）→ U方向重力, W/S（前後入力）→ X軸回転（ピッチ）→ V方向重力
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
        if (!showVelocityDebug || rtVelocityDebug == null) return;
        GUI.DrawTexture(new Rect(10, 10, debugDisplaySize, debugDisplaySize), rtVelocityDebug, ScaleMode.ScaleToFit, false);
    }
}
