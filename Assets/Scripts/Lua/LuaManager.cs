using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using XLua;
namespace Game.Lua
{
    /// <summary>
    /// Lua 管理器,处理 Lua 虚拟机的初始化与 Lua 文件的加载等
    /// </summary>
    class LuaManager : MonoBehaviour
    {
        public static LuaManager Instance { get; private set; }  // 唯一的实例
        
        private static readonly LuaEnv luaEnv = new LuaEnv();              // 所有的Lua脚本共享的环境,唯一
        private static readonly LuaTable sharedEnv = luaEnv.NewTable();    // 在多个Lua文件中共享的表,可以通过此表来通信

        public const string luaScriptsFolder = "LuaScripts/";
        public const string BundleresourcesPathName = "AssetsPackage/";
        public const string LuaScriptsRootPath = luaScriptsFolder;     // Lua脚本的根路径,注: 只可以用小写
        public const string LuaFileSuffix = ".lua.txt";                                       // Lua 脚本文件的后缀名,注: 只可以用小写
        
        // 保存所有Lua文件内存的缓冲,以减少Lua文件加载时的访问时间
        private readonly Dictionary<string, (byte[] luaContent, string luaFilePath)> mPrewarmedLuaFilesContent = new Dictionary<string, (byte[] luaContent, string luaFilePath)>();
        
        private readonly StringBuilder mCachedPathBuilder = new StringBuilder();    // 构建路径用的缓冲,减少GC

        private int mLuaEnvCount = 1;    // 标记一下LuaEnv的当前数量,用来计数什么时候释放它

        public void OnAddPrewarmLuaScript(TextAsset ta)
        {
            var taNames = ta.name.Split('.');
            var luaName = taNames[0];
            if(!mPrewarmedLuaFilesContent.ContainsKey(luaName)) {
                mPrewarmedLuaFilesContent.Add(luaName, (ta.bytes, null));
            }
            else
            {
                //this.LogErrorFormat("Error: lua script ({0}) has been added", luaName);
            }
            
        }

        /// <summary>
        /// 初始化 Lua 管理器,只可以初始化一次
        /// </summary>
        public static void InitLuaManager()
        {
            if (Instance != null)
                return;

            var luaGO = new GameObject("LuaManager");
            DontDestroyOnLoad(luaGO);
            luaGO.AddComponent<LuaManager>();
        }

        /// <summary>
        /// 分配一张新的Lua表. 注: 在这里集中管理,方便释放
        /// </summary>
        /// <returns></returns>
        public LuaTable NewTable()
        {
            mLuaEnvCount++;    // 计数++
            var scriptEnv = luaEnv.NewTable();
            // 为每个脚本设置一个独立的环境，可一定程度上防止脚本间全局变量、函数冲突
            LuaTable meta = luaEnv.NewTable();
            meta.Set("__index", luaEnv.Global);
            scriptEnv.SetMetaTable(meta);
            meta.Dispose();
            scriptEnv.Set("G", sharedEnv);
            return scriptEnv;
        }

        /// <summary>
        /// 执行给定的Lua脚本
        /// </summary>
        /// <param name="chunk"></param>
        /// <param name="chunkName"></param>
        /// <param name="env"></param>
        /// <returns></returns>
        public object[] DoString(byte[] chunk, string chunkName = "chunk", LuaTable env = null)
        {
            return luaEnv.DoString(chunk, chunkName, env);
        }

        public object[] DoString(string chunk, string chunkName = "chunk", LuaTable env = null)
        {
            return luaEnv.DoString(chunk, chunkName, env);
        }

        /// <summary>
        /// 自定义的 Lua 脚本加载函数
        /// </summary>
        /// <param name="luaScriptPath">指定要加载的 Lua 文件相对路径, 相对于 LuaScriptsRootPath 路径, 路径间以 / 分隔,
        /// 如 require 'LuaCommonFunc/CommonFunc' 表示将加载 Assets/BundleResources/LuaScripts/LuaCommonFunc/CommonFunc.lua.txt 文件 </param>
        /// <returns></returns>
        private byte[] MyCustomLoader(ref string luaScriptPath)
        {
            return _LoadLuaFile(luaScriptPath).luaContent;
        }

        /// <summary>
        /// 加载给定路径的 Lua 脚本
        /// </summary>
        /// <param name="luaScriptPath">指定要加载的 Lua 文件相对路径, 相对于 LuaScriptsRootPath 路径, 路径间以 / 分隔,
        /// 如 require 'LuaCommonFunc/CommonFunc' 表示将加载 Assets/BundleResources/LuaScripts/LuaCommonFunc/CommonFunc.lua.txt 文件 </param>
        /// <returns></returns>
        public static (byte[] luaContent, string luaFilePath) LoadLuaFile(in string luaScriptPath)
        {
            return Instance._LoadLuaFile(luaScriptPath);
        }

        // 定义xlua内置的脚本路径信息. 注: 一定要用小写. 第二参数设置为null表示由xlua在 xlua 的resources目录下加载
        private readonly (string luaPath, string realPath)[] mXLuaScripts =
        {
            ("XLua.Common.util",null)
            ,("XLua.Common.cs_coroutine",null)
            ,("XLua.Common.luapanda",null)
            ,("XLua.Common.debugtools",null)
            ,("libpdebug", null)
            //,("lua_structure_constants", "protocol/lua_structure_constants")
            //,("lua_enum_constants", "protocol/lua_enum_constants")
            //,("lua_protocol_constants", "protocol/lua_protocol_constants")
            //,("LuaProtocolImporter", "protocol/LuaProtocolImporter")
        };
        

        /// <summary>
        /// 加载
        /// </summary>
        /// <param name="luaScriptPath">指定要加载的Lua文件的相对路径或名称(不带后缀名)</param>
        /// <returns></returns>
        private (byte[] luaContent, string luaFilePath) _LoadLuaFile(in string luaScriptPath)
        {
            // var luaScriptRealPath = luaScriptPath.ToLower();    // 使用小写路径

            var luaScriptRealPath = luaScriptPath.Replace(".", "/");
            // 优先使用传入的路径来查找缓冲列表
            if (mPrewarmedLuaFilesContent.TryGetValue(luaScriptRealPath, out var luaFileContent))
            {  // 二级缓冲查找,减少性能开销
                return luaFileContent; // 缓冲列表中不包含Lua文件的全路径
            }
            
            // 构造全路径
            for (int index = mXLuaScripts.Length - 1; index >= 0; --index)
            { // 判定是否需要从 xLua 的 Resources 目录加载. 这些文件基本不会修改
                var info = mXLuaScripts[index];
                if (string.Compare(info.luaPath, luaScriptRealPath) == 0)
                {
                    if (string.IsNullOrEmpty(info.realPath))
                    {  // 由 xlua 在 Resources 目录下加载
                        mPrewarmedLuaFilesContent.Add(luaScriptRealPath, (null, null));    // 添加到缓冲中
                        return (null, null);
                    }

                    luaScriptRealPath = info.realPath;
                    break;
                }
            }

#if UNITY_EDITOR
            // 从文件中加载
            mCachedPathBuilder.Clear();
            mCachedPathBuilder.Append(LuaScriptsRootPath);
            mCachedPathBuilder.Append(luaScriptRealPath);
            mCachedPathBuilder.Append(LuaFileSuffix);        // 添加文件后缀名

            var luaFilePath = mCachedPathBuilder.ToString();
            var _luaPath = Path.Combine(Application.dataPath, luaFilePath);
            var bytes = SafeReadAllBytes(_luaPath);
            return (bytes, luaFilePath);
#else
            return (null,"");
#endif
        }
        public byte[] SafeReadAllBytes(string inFile)
        {
            try
            {
                if (string.IsNullOrEmpty(inFile))
                {
                    return null;
                }

                if (!File.Exists(inFile))
                {
                    return null;
                }

                File.SetAttributes(inFile, FileAttributes.Normal);
                return File.ReadAllBytes(inFile);
            }
            catch (System.Exception ex)
            {
                Debug.LogError(string.Format("SafeReadAllBytes failed! path = {0} with err = {1}", inFile, ex.Message));
                return null;
            }
        }
        private void Awake()
        {
            Debug.Assert(Instance == null);
            Instance = this;
            
            // 设置自定义的lua文件加载器
            luaEnv.AddLoader(MyCustomLoader);
            
            #if UNITY_EDITOR // 开启调试
            //luaEnv.DoString("require('Xlua.Common.luapanda').start('127.0.0.1',8818)", "");
            #endif

            // 开始一个预加载 Lua 文件的协程
            //StartCoroutine(PrewarmLuaFiles());
            
            // 开始 Tick
            //StartCoroutine(Tick());
        }
        
        private static WaitForSeconds wait = new WaitForSeconds(2);

        /// <summary>
        /// 我们自己做的Tick函数,用来控制Lua Tick()的频率,节省性能
        /// </summary>
        /// <returns></returns>
        private IEnumerator Tick()
        {
            while (!Instance.Equals(null))
            {
                yield return wait; // 等待一会
                luaEnv.Tick();
                luaEnv.FullGc();    // 我们每2秒GC,性能与内存占用能达到一个不错的平衡
            }
        }
        
        /// <summary>
        /// 预热加载所有的Lua文件
        /// </summary>
        /// <returns></returns>
        // private IEnumerator PrewarmLuaFiles()
        // {
        //     // Lua 加载器列表
        //     var listLuaLoadOH = new List<AsyncOperationHandle<TextAsset>>(100);
        //     
        //     // 获取资源定位器,从中查找Lua文件资源
        //     var resourceLocators = Addressables.ResourceLocators;
        //     foreach (var resLocator in resourceLocators)
        //     {
        //         var resKeys = resLocator.Keys;
        //         foreach (var resKey in resKeys)
        //         {
        //             if (resKey is string resPath && resPath.EndsWith(LuaFileSuffix))
        //             {
        //                 var luaKey = resPath;
        //                 if (mPrewarmedLuaFilesContent.ContainsKey(luaKey))
        //                 {  // 已经有缓冲了,跳过加载
        //                     continue;
        //                 }
        //                 var oh = Addressables.LoadAssetAsync<TextAsset>(resPath);
        //                 oh.Completed += (loadHandle) =>
        //                 {  // 处理Lua文件加载完成
        //                     mPrewarmedLuaFilesContent.Add(luaKey, 
        //                         loadHandle.Result ? (loadHandle.Result.bytes, string.Empty) : (null, null));
        //                 };
        //                 
        //                 listLuaLoadOH.Add(oh);
        //             }
        //         }
        //     }
        //     
        //     // 等待所有任务结束并释放掉所有的加载任务
        //     foreach (var luaLoadOH in listLuaLoadOH)
        //     {
        //         yield return luaLoadOH;
        //         Addressables.Release(luaLoadOH);
        //     }
        // }

        private void Update()
        {
            // 触发 Lua 脚本中注册的Update的函数调用
           // LuaEventManager.FireLuaEvent(LuaEvents.Update);
        }

        /// <summary>
        /// 用来释放 NewTable() 创建的LuaTable
        /// </summary>
        /// <param name="scriptEnv"></param>
        public void Dispose(LuaTable scriptEnv)
        {
            if (scriptEnv != null)
            {
                mLuaEnvCount--;
                scriptEnv.Dispose();
            }
            
            // 我们需要晚一点释放,因为要等其它的GameObject都释放后才可以,这样引用的 LuaEnv 才会以正常的顺序释放
            if (mLuaEnvCount > 0)
                return;

        #if UNITY_EDITOR
            luaEnv.DoString(@" 
                local util = require('XLua.Common.util')
                print('=== 输出还在被 C# 引用的Lua函数 ===')
                util.print_func_ref_by_csharp()
                print('======== 输出结束 ========')
            ");
        #endif
            
            sharedEnv.Dispose();
            luaEnv.Dispose();
            Instance = null;
        }

        private void OnDestroy()
        {
            mLuaEnvCount--;
            Dispose(null);
        }
    }
}