using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Musha {

/// <summary>
/// アセットバンドル読み込みクラス
/// </summary>
[AddComponentMenu("Musha/AssetBundleLoader")]
public partial class AssetBundleLoader : MonoBehaviour
{
	/// <summary>
	/// アセットバンドルをキャッシュしないフラグ
	/// </summary>
	[SerializeField]public bool dontCacheAssetBundle = false;
	/// <summary>
	/// 読み込みタスクの最大並列処理数
	/// </summary>
	[SerializeField]public int maxTaskProcessingSize = 8;
	/// <summary>
	/// サーバーのアセットバンドルディレクトリURL
	/// </summary>
	protected string serverAssetBundleDirectoryUrl = null;
	/// <summary>
	/// ローカルのリソースリストパス
	/// </summary>
	protected string localResourceListPath = null;
	/// <summary>
	/// サーバーのリソースリストURL
	/// </summary>
	protected string serverResourceListUrl = null;
	/// <summary>
	/// リソースリスト
	/// </summary>
	protected Dictionary<string, AssetBundleOperation> resourceList = new Dictionary<string, AssetBundleOperation>();
	/// <summary>
	/// 読み込みタスクリスト
	/// </summary>
	protected List<AssetLoadTaskBase> loadTaskList = new List<AssetLoadTaskBase>();

	/// <summary>
	/// Awake
	/// </summary>
	protected virtual void Awake()
	{
#if UNITY_EDITOR && !STREAMINGASSETS_SERVER
		this.SetServerAssetBundleDirectoryUrl("file://" + Application.dataPath.Replace("Assets", "Server/" + Define.assetBundleDirectoryName));
#else
		this.SetServerAssetBundleDirectoryUrl("file://" + Application.streamingAssetsPath + "/" + Define.assetBundleDirectoryName);
#endif
	}

	/// <summary>
	/// サーバーのアセットバンドルディレクトリURLを設定する
	/// </summary>
	public void SetServerAssetBundleDirectoryUrl(string url)
	{
		this.serverAssetBundleDirectoryUrl = url;
		this.serverResourceListUrl = url + "/ResourceList.csv";
		this.localResourceListPath = Define.GetLocalAssetBundleDirectoryPath() + "/ResourceList.dat";
	}

	/// <summary>
	/// セットアップ
	/// </summary>
	public void Setup(Action<int> onFinished)
	{
		if (!this.dontCacheAssetBundle)
		{
			//ローカルのリソースリスト読み込み
			this.LoadResourceList();
		}

		//サーバーから最新のリソースリストを取得して更新
		StartCoroutine(this.DownloadResourceList(onFinished));
	}

	/// <summary>
	/// リソースリスト保存
	/// </summary>
	private void SaveResourceList()
	{
		//ディレクトリ作成
		Directory.CreateDirectory(Path.GetDirectoryName(this.localResourceListPath));

		//ファイル書き込み
		using (var stream = new FileStream(this.localResourceListPath, FileMode.Create, FileAccess.Write))
		using (var writer = new BinaryWriter(stream))
		{
			foreach (var data in this.resourceList.Values)
			{
				data.Save(writer);
			}
		}
	}

	/// <summary>
	/// リソースリスト読み込み
	/// </summary>
	protected void LoadResourceList()
	{
		//ファイル存在チェック
		if (File.Exists(this.localResourceListPath))
		{
			//ファイル読み込み
			using (var stream = new MemoryStream(File.ReadAllBytes(this.localResourceListPath)))
			using (var reader = new BinaryReader(stream))
			{
				while (!reader.IsEnd())
				{
					var data = new AssetBundleOperation(reader);
					this.resourceList.Add(data.name, data);
				}
			}
		}
	}

	/// <summary>
	/// リソースリストのダウンロード
	/// </summary>
	protected IEnumerator DownloadResourceList(Action<int> onFinished = null)
	{
		//CSVダウンロード
		using (var www = new WWW(this.serverResourceListUrl))
		{
			//ダウンロード完了を待つ
			yield return www.WaitOrTimeout();

			//タイムアウト
			if (!www.isDone)
			{
				Debug.LogError("タイムアウト");
			}
			//エラー
			else if (!string.IsNullOrEmpty(www.error))
			{
				Debug.LogWarning("リソースリストのダウンロードに失敗 : " + www.error);
				onFinished.SafetyInvoke(-1);
			}
			else
			{
				//CSV読み込み
				using (var stream = new MemoryStream(www.bytes))
				using (var reader = new StreamReader(stream))
				{
					string line = null;
					while ((line = reader.ReadLine()) != null)
					{
						string[] lineSplit = line.Split(',');
						string name = lineSplit[0];

						if (this.resourceList.ContainsKey(name))
						{
							this.resourceList[name].UpdateFromCsv(lineSplit);
						}
						else
						{
							var data = new AssetBundleOperation(lineSplit);
							this.resourceList.Add(name, data);
						}
					}
				}

				if (!this.dontCacheAssetBundle)
				{
					//更新内容を保存
					this.SaveResourceList();
				}

				//コールバック実行
				onFinished.SafetyInvoke(1);
			}
		}	
	}

	/// <summary>
	/// 単体アセット読み込み
	/// </summary>
	/// <param name="assetBundleName">アセットバンドル名</param>
	/// <param name="assetName">読み込むアセット名</param>
	/// <param name="onLoad">読み込み完了時コールバック</param>
	public void LoadAsset<T>(string assetBundleName, string assetName, Action<T> onLoad = null) where T : UnityEngine.Object
	{
		if (!this.CheckAssetBundleExists(assetBundleName))
		{
			onLoad.SafetyInvoke(null);
			return;
		}

		var data = this.resourceList[assetBundleName];
		var assetOperation = data.FindAssetOperation<AssetOperation<T>>(assetName);

		//初めての読み込み
		if (assetOperation == null)
		{
			//アセット管理データ作成
			assetOperation = new AssetOperation<T>(assetName, onLoad);
			data.AddAssetOperation(assetOperation);
			//読み込み開始
			this.UpdateAssetBundleOperation(data);
		}
		//ロード済み
		else if (assetOperation.GetStatus() == AssetOperationBase.Status.isLoaded)
		{
			//１フレーム後にコールバック実行
			StartCoroutine(CoroutineUtility.WaitForFrameAction(1, () =>
			{
				onLoad.SafetyInvoke(assetOperation.GetAsset());
			}));
		}
		//ロード中
		else
		{
			//コールバック追加
			assetOperation.AddCallBack(onLoad);
		}
	}

	/// <summary>
	/// 全アセット読み込み
	/// </summary>
	/// <param name="assetBundleName">アセットバンドル名</param>
	/// <param name="onLoad">読み込み完了時コールバック</param>
	public void LoadAllAssets<T>(string assetBundleName, Action<T[]> onLoad = null) where T : UnityEngine.Object
	{
		if (!this.CheckAssetBundleExists(assetBundleName))
		{
			onLoad.SafetyInvoke(null);
			return;
		}

		var data = this.resourceList[assetBundleName];
		var assetOperation = data.FindAssetOperation<AllAssetsOperation<T>>();

		//初めての読み込み
		if (assetOperation == null)
		{
			//アセット管理データ作成
			assetOperation = new AllAssetsOperation<T>(onLoad);
			data.AddAssetOperation(assetOperation);
			//読み込み開始
			this.UpdateAssetBundleOperation(data);
		}
		//ロード済み
		else if (assetOperation.GetStatus() == AssetOperationBase.Status.isLoaded)
		{
			//１フレーム後にコールバック実行
			StartCoroutine(CoroutineUtility.WaitForFrameAction(1, () =>
			{
				onLoad.SafetyInvoke(assetOperation.GetAllAssets());
			}));
		}
		//ロード中
		else
		{
			//コールバック追加
			assetOperation.AddCallBack(onLoad);
		}
	}

	/// <summary>
	/// サブアセット読み込み
	/// </summary>
	/// <param name="assetBundleName">アセットバンドル名</param>
	/// <param name="assetName">読み込むアセット名</param>
	/// <param name="onLoad">読み込み完了時コールバック</param>
	public void LoadSubAssets<T>(string assetBundleName, string assetName, Action<T[]> onLoad = null) where T : UnityEngine.Object
	{
		if (!this.CheckAssetBundleExists(assetBundleName))
		{
			onLoad.SafetyInvoke(null);
			return;
		}

		var data = this.resourceList[assetBundleName];
		var assetOperation = data.FindAssetOperation<SubAssetsOperation<T>>(assetName);

		//初めての読み込み
		if (assetOperation == null)
		{
			//アセット管理データ作成
			assetOperation = new SubAssetsOperation<T>(assetName, onLoad);
			data.AddAssetOperation(assetOperation);
			//読み込み開始
			this.UpdateAssetBundleOperation(data);
		}
		//ロード済み
		else if (assetOperation.GetStatus() == AssetOperationBase.Status.isLoaded)
		{
			//１フレーム後にコールバック実行
			StartCoroutine(CoroutineUtility.WaitForFrameAction(1, () =>
			{
				onLoad.SafetyInvoke(assetOperation.GetAllAssets());
			}));
		}
		//ロード中
		else
		{
			//コールバック追加
			assetOperation.AddCallBack(onLoad);
		}
	}

	/// <summary>
	/// シーンアセットバンドルの読み込み
	/// </summary>
	/// <param name="assetBundleName">アセットバンドル名</param>
	/// <param name="onLoad">読み込み完了時コールバック</param>
	public void LoadScenePaths(string assetBundleName, Action<string[]> onLoad = null)
	{
		if (!this.CheckAssetBundleExists(assetBundleName))
		{
			onLoad.SafetyInvoke(null);
			return;
		}

		AssetBundleOperation data = this.resourceList[assetBundleName];
		var assetOperation = data.FindAssetOperation<SceneAssetOperation>();

		//初めての読み込み
		if (assetOperation == null)
		{
			//アセット管理データさくせい
			assetOperation = new SceneAssetOperation(onLoad);
			data.AddAssetOperation(assetOperation);
			//読み込み開始
			this.UpdateAssetBundleOperation(data);
		}
		//ロード済み
		else if (assetOperation.GetStatus() == AssetOperationBase.Status.isLoaded)
		{
			//１フレーム後にコールバック実行
			StartCoroutine(CoroutineUtility.WaitForFrameAction(1, () =>
			{
				onLoad.SafetyInvoke(assetOperation.GetAllScenePaths());
			}));
		}
		//ロード中
		else
		{
			//コールバック追加
			assetOperation.AddCallBack(onLoad);
		}
	}

	/// <summary>
	/// アセットバンドルの状態に応じた処理
	/// </summary>
	private void UpdateAssetBundleOperation(AssetBundleOperation data)
	{
		var status = data.GetStatus();

		switch (status)
		{
		//ダウンロードが必要
		case AssetBundleOperation.Status.isNeedDownload:
		{
			//ダウンロード開始
			data.DownloadAssetBundle(this, this.serverAssetBundleDirectoryUrl, (bytes) =>
			{
				//ダウンロードしたアセットバンドルをローカルに保存しない場合
				if (this.dontCacheAssetBundle)
				{
					//メモリからアセットバンドルを読み込む
					data.LoadAssetBundleFromMemory(bytes, () =>
					{
						this.UpdateAssetBundleOperation(data);
					});
				}
				//ダウンロードしたアセットバンドルをローカルに保存する場合
				else
				{
					//保存先ディレクトリを作成
					Directory.CreateDirectory(Path.GetDirectoryName(data.path));
					//アセットバンドルを保存
					File.WriteAllBytes(data.path, bytes);
					//ダウンロードしたのでCRC値を更新
					data.UpdateCRC();
					//更新内容を保存
					this.SaveResourceList();
					//次の処理へ
					this.UpdateAssetBundleOperation(data);
				}
			});
		}
		break;

		//ダウンロード済み
		case AssetBundleOperation.Status.isDownloaded:
		{
			//ローカルファイルからアセットバンドルを読み込む
			data.LoadAssetBundleFromFile(() =>
			{
				this.UpdateAssetBundleOperation(data);
			});
		}
		break;

		//読み込み済み
		case AssetBundleOperation.Status.isLoaded:
		{
			data.LoadAsset();
		}
		break;
		}
	}

	/// <summary>
	/// 読み込み済み単体アセットの取得
	/// </summary>
	/// <param name="assetBundleName">取得したいアセットが含まれているアセットバンドル名</param>
	/// <param name="assetName">取得したいアセット名</param>
	public T GetLoadedAsset<T>(string assetBundleName, string assetName) where T : UnityEngine.Object
	{
		AssetOperation<T> assetOperation = null;

		if (this.CheckAssetLoaded<AssetOperation<T>>(assetBundleName, assetName, out assetOperation))
		{
			return assetOperation.GetAsset();
		}
		else
		{
			return null;
		}
	}

	/// <summary>
	/// 読み込み済み全体アセットの取得
	/// </summary>
	/// <param name="assetBundleName">アセットバンドル名</param>
	public T[] GetLoadedAllAssets<T>(string assetBundleName) where T : UnityEngine.Object
	{
		AllAssetsOperation<T> assetOperation = null;

		if (this.CheckAssetLoaded<AllAssetsOperation<T>>(assetBundleName, null, out assetOperation))
		{
			return assetOperation.GetAllAssets();
		}
		else
		{
			return null;
		}
	}

	/// <summary>
	/// 読み込み済みサブアセットの取得
	/// </summary>
	/// <param name="assetBundleName">取得したいサブアセットが含まれているアセットバンドル名</param>
	/// <param name="assetName">取得したいサブアセット名</param>
	public T[] GetLoadedSubAssets<T>(string assetBundleName, string assetName) where T : UnityEngine.Object
	{
		SubAssetsOperation<T> assetOperation = null;

		if (this.CheckAssetLoaded<SubAssetsOperation<T>>(assetBundleName, assetName, out assetOperation))
		{
			return assetOperation.GetAllAssets();
		}
		else
		{
			return null;
		}
	}

	/// <summary>
	/// アセットバンドルのUnloadフラグ設定
	/// </summary>
	public void SetAssetBundleDontUnloadFlag(string assetBundleName, bool isDontUnload)
	{
		if (this.CheckAssetBundleExists(assetBundleName))
		{
			this.resourceList[assetBundleName].isDontUnload = isDontUnload;
		}
	}

	/// <summary>
	/// 指定アセットバンドルの破棄
	/// </summary>
	public void UnloadAssetBundle(string assetBundleName)
	{
		if (this.CheckAssetBundleExists(assetBundleName))
		{
			this.resourceList[assetBundleName].Unload();
		}
	}

	/// <summary>
	/// 全アセットバンドルの破棄
	/// </summary>
	public void UnloadAll()
	{
		foreach (var data in this.resourceList.Values)
		{
			data.Unload();
		}
	}

	/// <summary>
	/// 指定のアセットバンドルが処理中かどうか
	/// ※true時にはUnload出来ない
	/// </summary>
	public bool IsBusy(string assetBundleName)
	{
		if (this.CheckAssetBundleExists(assetBundleName))
		{
			return this.resourceList[assetBundleName].IsBusy();
		}
		return false;
	}

	/// <summary>
	/// 処理中のアセットバンドルがあるかどうか
	/// ※true時にはUnload出来ないものがある
	/// </summary>
	public bool IsBusy()
	{
		return this.resourceList.Values.Any(x => x.IsBusy());
	}

	/// <summary>
	/// 読み込みタスク追加
	/// </summary>
	public void AddTask(AssetLoadTaskBase task)
	{
		if (!this.loadTaskList.Contains(task))
		{
			//読み込み完了時コールバックの追加
			task.AddCallBack(() =>
			{
				//自身をリストから除去
				this.loadTaskList.Remove(task);
				//残っているタスクの処理を開始
				this.StartTask();
			});

			//リストに追加
			this.loadTaskList.Add(task);
		}
	}

	/// <summary>
	/// 読み込みタスクの処理を開始する
	/// </summary>
	public void StartTask()
	{
		//処理中タスク数が最大数未満かどうか
		if (this.loadTaskList.Count(x => x.status == AssetLoadTaskBase.Status.isLoading) < this.maxTaskProcessingSize)
		{
			for (int i = 0, imax = this.loadTaskList.Count; i < imax; i++)
			{
				//処理前のタスクを検索
				if (this.loadTaskList[i].status == AssetLoadTaskBase.Status.None)
				{
					//処理を開始
					this.loadTaskList[i].Load(this);
					//まだ余裕があるなら他のタスクの処理も開始する
					this.StartTask();
					break;
				}
			}
		}
	}

	/// <summary>
	/// 読み込みタスクの追加と同時に処理の開始
	/// </summary>
	public void AddAndStartTask(AssetLoadTaskBase task)
	{
		this.AddTask(task);
		this.StartTask();
	}

	/// <summary>
	/// 積まれている読み込みタスク数
	/// </summary>
	public int GetTaskCount()
	{
		return this.loadTaskList.Count;
	}

	/// <summary>
	/// 積んである読み込みタスクを消す
	/// </summary>
	public void ClearTask()
	{
		this.loadTaskList.Clear();
	}

	/// <summary>
	/// リソースリストに存在するアセットバンドルかどうかのチェック
	/// </summary>
	private bool CheckAssetBundleExists(string assetBundleName)
	{
#if MUSHA_DEBUG
		if (!this.resourceList.ContainsKey(assetBundleName))
		{
			Debug.LogWarningFormat("リソースリストに無いアセットバンドルです：assetBundleName={0}", assetBundleName);
			return false;
		}
#endif
		return true;
	}

	/// <summary>
	/// アセットが読み込み済みかどうかのチェック
	/// </summary>
	private bool CheckAssetLoaded<T>(string assetBundleName, string assetName, out T assetOperation) where T : AssetOperationBase
	{
		if (!this.CheckAssetBundleExists(assetBundleName))
		{
			assetOperation = null;
			return false;
		}

		var data = this.resourceList[assetBundleName];
		assetOperation = data.FindAssetOperation<T>(assetName);
#if MUSHA_DEBUG
		if (assetOperation == null || assetOperation.GetStatus() != AssetOperationBase.Status.isLoaded)
		{
			Debug.LogWarningFormat(
				"アセットが存在しないか、読み込まれていません。\n" +
				"assetBundleName = {0}\n" +
				"assetName = {1}\n" +
				"assetOperationType = {2}",
				assetBundleName,
				assetName,
				typeof(T));
			return false;
		}
#endif
		return true;
	}

#if UNITY_EDITOR
	/// <summary>
	/// AssetBundleLoaderのカスタムインスペクター
	/// </summary>
	[CustomEditor(typeof(AssetBundleLoader))]
	private class AssetBundleLoaderInspector : Editor
	{
		/// <summary>
		/// リソースリスト折り畳み表示用
		/// </summary>
		private bool foldoutResourceList = false;
		/// <summary>
		/// 読み込み済みアセットバンドル折り畳み表示用
		/// </summary>
		private bool foldoutLoadedAssetBundles = false;
		/// <summary>
		/// 読み込みタスク折り畳み表示用
		/// </summary>
		private bool foldoutLoadTaskList = false;

		/// <summary>
		/// インスペクター描画
		/// </summary>
		public override void OnInspectorGUI()
		{
			var target = (AssetBundleLoader)this.target;

			EditorGUI.indentLevel = 0;

			base.OnInspectorGUI();

			//サーバーのアセットバンドルディレクトリURL表示
			EditorGUILayout.LabelField("ServerAssetBundleDirectoryUrl");
			EditorGUILayout.TextField(target.serverAssetBundleDirectoryUrl);

			//ローカルのリソースリストパス
			EditorGUILayout.LabelField("LocalResourceListPath");
			EditorGUILayout.TextField(target.localResourceListPath);

			//サーバーのリソースリストURL
			EditorGUILayout.LabelField("ServerResourceListUrl");
			EditorGUILayout.TextField(target.serverResourceListUrl);

			//リソースリスト一覧表示
			this.foldoutResourceList = EditorGUILayout.Foldout(this.foldoutResourceList, "ResourceList:Count=" + target.resourceList.Count);
			if (this.foldoutResourceList)
			{
				if (target.resourceList.Count == 0)
				{
					EditorGUI.indentLevel = 1;
					EditorGUILayout.LabelField("empty");
				}
				else
				{
					foreach (var data in target.resourceList.Values)
					{
						EditorGUI.indentLevel = 1;
						data.OnInspectorGUI();
					}
				}
			}

			EditorGUI.indentLevel = 0;

			//読み込み済みアセットバンドル一覧表示
			this.foldoutLoadedAssetBundles = EditorGUILayout.Foldout(this.foldoutLoadedAssetBundles, "LoadedAssetBundles");
			if (this.foldoutLoadedAssetBundles)
			{
				var loadedAssetBundles = target.resourceList.Values.Where(x => x.GetStatus() == AssetBundleOperation.Status.isLoaded);
				if (loadedAssetBundles.Count() == 0)
				{
					EditorGUI.indentLevel = 1;
					EditorGUILayout.LabelField("empty");
				}
				else
				{
					foreach (var data in loadedAssetBundles)
					{
						EditorGUI.indentLevel = 1;
						data.isDontUnload = EditorGUILayout.ToggleLeft(data.name, data.isDontUnload, EditorStyles.textField);
					}
				}
			}

			EditorGUI.indentLevel = 0;

			//読み込みタスク一覧表示
			this.foldoutLoadTaskList = EditorGUILayout.Foldout(this.foldoutLoadTaskList, "AssetLoadTaskList:Count=" + target.loadTaskList.Count);
			if (this.foldoutLoadTaskList)
			{
				if (target.loadTaskList.Count == 0)
				{
					EditorGUI.indentLevel = 1;
					EditorGUILayout.LabelField("empty");
				}
				else
				{
					for (int i = 0, imax = target.loadTaskList.Count; i < imax; i++)
					{
						EditorGUI.indentLevel = 1;
						target.loadTaskList[i].OnInspectorGUI(i);
					}
				}
			}

			this.Repaint();
		}
	}
#endif
}

}//namespace Musha
