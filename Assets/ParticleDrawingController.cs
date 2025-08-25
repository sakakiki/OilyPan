using UnityEngine;
using UnityEngine.Rendering;
using System.IO;

public class ParticleDrawingController_Debug : MonoBehaviour
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

    // デバッグ切替（ファイル保存や Readback は高コストなので任意で有効化）
    [Header("デバッグ")]
    [SerializeField] private bool enableDebugReadback = true;
    [SerializeField] private float savePngInterval = 1.0f; // PNG 保存間隔(秒)

    // --- 内部状態 ---
    private RenderTexture rtHeight;
    private RenderTexture rtNormal;
    private Mesh quadMesh;
    private Vector3[] particlePositions;
    private Matrix4x4[] matrices;
    private CommandBuffer commandBuffer;

    // デバッグ用のタイマーと読み出し用テクスチャ（再利用）
    private float lastSaveTime = 0f;
    private Texture2D readbackTex = null;

    void Start()
    {
        // ---------- RenderTexture の初期化 ----------
        // 深度不要なので depth = 0。デバッグしやすいよう ARGBHalf を使用。
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
            // 初期位置は狭めの範囲にランダム配置（必要に応じて調整）
            particlePositions[i] = new Vector3(Random.Range(-0.1f, 0.1f), Random.Range(-0.1f, 0.1f), 0);
            matrices[i] = Matrix4x4.identity;
        }

        // ---------- クワッド Mesh の作成 ----------
        // （元の実装に合わせて両面描画用に逆向き三角形も追加しています）
        quadMesh = new Mesh();
        quadMesh.vertices = new Vector3[] {
            new Vector3(-0.5f, -0.5f, 0),
            new Vector3( 0.5f, -0.5f, 0),
            new Vector3(-0.5f,  0.5f, 0),
            new Vector3( 0.5f,  0.5f, 0),
        };
        quadMesh.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        // 正向き三角形 + 逆向きコピー（裏面も描く）
        quadMesh.triangles = new int[] {
            0,2,1, 2,3,1, // 表
            1,2,0, 1,3,2  // 裏（元実装に合わせて二重書き）
        };
        quadMesh.RecalculateBounds();

        // CommandBuffer を用意（Update でコマンドを積み、Graphics.ExecuteCommandBuffer で実行）
        commandBuffer = new CommandBuffer { name = "Particle Drawing Debug" };

        // normalMaterial に事前に TexelSize/Strength をセット（Blit 時に使う）
        if (normalMaterial != null)
        {
            normalMaterial.SetFloat("_Strength", 1.0f);
            normalMaterial.SetVector("_TexelSize", new Vector4(1.0f / textureResolution, 1.0f / textureResolution, 0, 0));
        }

        // デバッグ用読み出し Texture2D を必要時に作成して再利用（毎フレームの GC を防ぐ）
        if (enableDebugReadback)
        {
            readbackTex = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
        }

        Debug.Log("[DEBUG] ParticleDrawingController_Debug started. RT sizes: " + textureResolution + "x" + textureResolution);
    }

    void Update()
    {
        // 1) 位置更新（CPU側）：入力に応じて全パーティクルを移動
        UpdateParticlePositions();

        // 2) コマンドバッファに描画命令を積む（全インスタンスを個別 DrawMesh で積む）
        //    → 確実に RT に書き込ませるための実装（デバッグ用、重い）
        commandBuffer.Clear();
        commandBuffer.SetRenderTarget(rtHeight);
        commandBuffer.SetViewport(new Rect(0, 0, textureResolution, textureResolution));
        // 深度は無いので false。カラーのみクリア。
        commandBuffer.ClearRenderTarget(false, true, Color.black);

        // 投影・ビュー行列（パーティクル座標 -0.5..0.5 に合わせた正射影）
        Matrix4x4 proj = Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, -1f, 1f);
        Matrix4x4 view = Matrix4x4.TRS(new Vector3(0, 0, -0.5f), Quaternion.identity, Vector3.one).inverse;
        commandBuffer.SetViewProjectionMatrices(view, proj);

        // 各パーティクルを個別に DrawMesh（重いが確実）
        for (int i = 0; i < particleCount; i++)
        {
            // shaderPass 0 を指定
            commandBuffer.DrawMesh(quadMesh, matrices[i], heightMaterial, 0, 0);
        }

        // コマンドを GPU に送って実行
        Graphics.ExecuteCommandBuffer(commandBuffer);

        // 3) 高さ RT を元に法線 RT を生成（Blit）
        //    normalMaterial は _MainTex を参照する想定。TexelSize 等は Start で設定済み。
        Graphics.Blit(rtHeight, rtNormal, normalMaterial);

        // 4) デバッグ読み出し・保存（有効時のみ・インターバルで制御）
        if (enableDebugReadback && Time.time - lastSaveTime >= savePngInterval)
        {
            // 中心ピクセルを読んでログ出力
            Color centerH = ReadPixelFromRT(rtHeight, textureResolution / 2, textureResolution / 2);
            Color centerN = ReadPixelFromRT(rtNormal, textureResolution / 2, textureResolution / 2);
            Debug.Log($"[DEBUG READBACK] rtHeight center: {centerH} | rtNormal center: {centerN}");

            // PNG 保存（I/O は高コストなのでデバッグ時のみ短い間隔で）
            SaveRTToPNG(rtHeight, "rtHeight_after_cmdloop.png");
            SaveRTToPNG(rtNormal, "rtNormal_after_blit.png");

            lastSaveTime = Time.time;
        }
    }

    // パーティクル位置の更新（外部入力により移動させています）
    void UpdateParticlePositions()
    {
        Vector3 moveDirection = Vector3.zero;
        moveDirection.x = Input.GetAxis("Horizontal");
        moveDirection.y = Input.GetAxis("Vertical");
        moveDirection.Normalize();

        for (int i = 0; i < particleCount; i++)
        {
            // 毎フレームの Random.Range は負荷になるため可能なら事前生成するのが望ましい。
            particlePositions[i] += moveDirection * Time.deltaTime * Random.Range(0.1f, 0.5f);

            matrices[i] = Matrix4x4.TRS(
                particlePositions[i],
                Quaternion.identity,
                new Vector3(particleSize, particleSize, particleSize)
            );
        }
    }

    // RenderTexture の単一ピクセルを読み取り（readbackTex を再利用してアロケーションを抑える）
    private Color ReadPixelFromRT(RenderTexture rt, int x, int y)
    {
        if (readbackTex == null)
        {
            // デバッグ無効化時に呼ばれることを防ぐために遅延生成
            readbackTex = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
        }

        RenderTexture old = RenderTexture.active;
        RenderTexture.active = rt;

        // readbackTex を使い回して ReadPixels
        readbackTex.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
        readbackTex.Apply();
        Color c = readbackTex.GetPixel(0, 0);

        RenderTexture.active = old;
        return c;
    }

    // RenderTexture を PNG に保存（デバッグ用）
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
        if (commandBuffer != null) commandBuffer.Release();
        if (readbackTex != null) Destroy(readbackTex);
    }
}
