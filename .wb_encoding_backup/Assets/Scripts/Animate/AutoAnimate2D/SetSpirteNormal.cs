using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// 实时刷新2D的法线，因为2D角色需要法线，但是动画只会穿主纹理，
/// 因此法线需要额外传递，反正Sprite会自动匹配UV，只需要传入法线即可
/// </summary>
public class SetSpirteNormal : MonoBehaviour
{
    SpriteRenderer spriteRenderer;
    [SerializeField]
    List<Texture> normal;

    Dictionary<string, Texture> spriteMaps;
    string currentName;


    private void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (normal == null) return;
        spriteMaps = new Dictionary<string, Texture>();
        for(int i=0; i<normal.Count; i++)
        {
            string name = normal[i].name.Substring(0, normal[i].name.Length - 7);
            spriteMaps[name] = normal[i];
        }
    }

    private void Update()
    {
        if (spriteMaps == null) return;
        string spriteName = spriteRenderer.sprite.name;
        string thisName = spriteName.Substring(0, spriteName.LastIndexOf('_'));
        if (thisName != currentName)
        {
            Texture texture;
            if(spriteMaps.TryGetValue(thisName, out texture))
            {
                spriteRenderer.material.SetTexture("_NormalMap", texture);
                currentName = thisName;
            }
            else
            {
                Debug.LogError(thisName + " " + spriteName);
            }
        }
    }
}
