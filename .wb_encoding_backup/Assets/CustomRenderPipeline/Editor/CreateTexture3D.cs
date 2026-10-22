using Cluster;
using UnityEditor;
using UnityEngine;

public class ExampleEditorScript : EditorWindow
{
    [MenuItem("MyTool/3DTexture")]
    static void CreateTexture3D()
    {
        // 配置纹理
        int size = 32;
        TextureFormat format = TextureFormat.RGBA32;
        TextureWrapMode wrapMode = TextureWrapMode.Clamp;

        // 创建纹理并应用配置
        Texture3D texture = new Texture3D(size, size, size, format, false);
        texture.wrapMode = wrapMode;

        // 创建 3 维数组以存储颜色数据
        Color[] colors = new Color[size * size * size];

        // 填充数组，使纹理的 x、y 和 z 值映射为红色、蓝色和绿色
        float inverseResolution = 1.0f / (size - 1.0f);
        for (int z = 0; z < size; z++)
        {
            int zOffset = z * size * size;
            for (int y = 0; y < size; y++)
            {
                int yOffset = y * size;
                for (int x = 0; x < size; x++)
                {
                    colors[x + yOffset + zOffset] = new Color(x * inverseResolution,
                        y * inverseResolution, z * inverseResolution, 1.0f);
                }
            }
        }

        // 将颜色值复制到纹理
        texture.SetPixels(colors);

        // 将更改应用到纹理，然后将更新的纹理上传到 GPU
        texture.Apply();

        // 将纹理保存到 Unity 项目
        AssetDatabase.CreateAsset(texture, "Assets/Example3DTexture.asset");
    }

    [MenuItem("MyTool/Log Visual Tex node count")]
    static void LogVisualTextureNode()
    {
        Debug.Log(TextureNodesMap.Instance.AllTexturesNodeCount);
    }
}