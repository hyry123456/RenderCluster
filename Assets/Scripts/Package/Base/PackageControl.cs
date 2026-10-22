using System.Collections.Generic;

namespace Package
{
    /// <summary>/// 背包类的加载需要的数据结构体/// </summary>
    public struct PackageLoadData
    {
        /// <summary>   /// 所有的背包的类名称     /// </summary>
        public List<string> packageClassNames;
        /// <summary>   /// 所有获取的物件以及数量   /// </summary>
        public List<KeyValuePair<string, string>> obtainItemAndCounts;
    }

    /// <summary>
    /// 背包控制类，所有的背包数据都在这里进行控制
    /// </summary>
    public class PackageControl
    {
        private static PackageControl instance;
        public static PackageControl Instance
        {
            get
            {
                if(instance == null)
                    instance = new PackageControl();
                return instance;
            }
        }
        private PackageControl()
        {

        }
        /// <summary>
        /// 所有背包的映射表，<类名，数量>，暂时先这样设置先
        /// </summary>
        private Dictionary<string, int> packagesMap;

        public void LoadPackageData(PackageLoadData packageData)
        {
            SetAllPackage(packageData.packageClassNames);
            SetItemCount(packageData.obtainItemAndCounts);
        }

        /// <summary> /// 设置背包数据，也就是给背包映射表进行初始化/// </summary>
        /// <param name="packageClassNames">所有背包的类名称数组</param>
        private void SetAllPackage(List<string> packageClassNames)
        {
            packagesMap?.Clear();
            if (packageClassNames == null) return;
            packagesMap = new Dictionary<string, int>(packageClassNames.Count);
            for(int i=0; i < packageClassNames.Count; i++)
            {
                packagesMap.Add(packageClassNames[i], 0);
            }
        }

        /// <summary>  /// 设置每一个已经获取的物件的数量    /// </summary>
        /// <param name="itemsData">所有获取的物件的数量</param>
        private void SetItemCount(List<KeyValuePair<string, string>> itemsData)
        {
            if (itemsData == null) return;

            foreach (var item in itemsData)
            {
                packagesMap[item.Key] = int.Parse(item.Value);
            }
        }

    }
}