# P_QA.Web 品保流程管理平台

整合四套原 VB6 系統，使用既有 SQL Server 資料表，不搬移或重建正式資料：

- 各部門會辦
- 抱怨單（含詳細內容與假日檔）
- 開發試製案（同一案號多筆物料明細）
- 變更通知單

## 架構

- ASP.NET Core 10 Web API
- Microsoft.Data.SqlClient 參數化 SQL
- AES-256-CBC + HMAC-SHA256 安全設定
- HttpOnly Cookie／Bearer Token／8 小時 Session
- Vanilla JavaScript 響應式中文 Web 介面
- NAS 附件上傳、下載及刪除
- ONNX Runtime + ImageSharp 的 CPU 以圖搜圖（啟動時載入記憶體、PLINQ 平行檢索）

## 執行

發佈資料夾執行 `P_QA.Web.exe`，瀏覽器開啟 `http://localhost:5220`。

IIS 部署時使用 `No Managed Code`、x64 應用程式集區，網站實體路徑指向 publish 資料夾。`connections.aes` 與 `connections.key` 必須成對部署並限制讀取權限。

## NAS 設定

附件根目錄為 `\\nasts469\東正舊系統`。若 IIS 應用程式集區帳號本身沒有 NAS 權限，停止網站後執行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Set-QaNasCredentials.ps1
```

設定完成後重新啟動網站。

## 以圖搜圖設定

專案已提供 Controller API、登入後的「以圖搜圖」操作頁，以及本機 `OnnxModels/vision_model.onnx` 的 CLIP ViT-B/32 INT8 模型。模型檔不納入一般 Git 提交，但會由 Visual Studio／`dotnet publish` 自動複製到發佈資料夾。設定正式官方圖庫路徑後，請在 `appsettings.json` 設定：

```json
"ImageSearch": {
  "Enabled": true,
  "ModelPath": "OnnxModels/vision_model.onnx",
  "LibraryRoot": "D:\\OfficialImages",
  "DefaultFolder": "",
  "VectorCachePath": "image-search-cache/vectors.bin",
  "InputName": "pixel_values",
  "OutputName": "image_embeds",
  "InputWidth": 224,
  "InputHeight": 224,
  "Mean": [ 0.48145466, 0.4578275, 0.40821073 ],
  "Std": [ 0.26862954, 0.26130258, 0.27577711 ]
}
```

- `LibraryRoot` 是 IIS 主機可讀取的官方圖片根目錄；使用者只能選擇此根目錄內的資料夾。
- 上例是 CLIP 常用的 RGB 正規化值。SigLIP 或其他匯出模型請依模型規格調整節點名稱、輸入尺寸、`Mean` 與 `Std`。
- 模型輸出必須是單張圖片的一維 embedding（例如 `[1, 512]`），服務會做 L2 正規化後以內積計算 cosine similarity。
- `IndexDegreeOfParallelism`、`SearchDegreeOfParallelism` 設為 `0` 時使用 CPU 邏輯核心數，也可指定固定值避免 IIS 主機過度使用 CPU。
- 啟用後，網站啟動會等待圖庫索引完成才開始服務。首次執行會推論全部圖片；後續啟動會從 `image-search-cache/vectors.bin` 重用未變更圖片的向量，並一次載入記憶體。
- IIS 應用程式集區身分需有圖庫讀取權限，以及 `image-search-cache` 資料夾的讀寫權限。

Controller API：

- `GET /api/image-search/status`：索引狀態與圖片筆數。
- `GET /api/image-search/folders`：可選擇的圖庫子資料夾。
- `POST /api/image-search/index/rebuild`：切換資料夾並重建記憶體索引。
- `POST /api/image-search/search?topK=12`：以 `multipart/form-data` 的 `file` 欄位上傳查詢圖片。
- `GET /api/image-search/images/{imageId}`：讀取命中的官方圖片。

上述 API 沿用 P_QA.Web 現有登入驗證。向量比對階段不會再次執行 5,800 張圖片的 ONNX 推論，而是在記憶體中以 PLINQ 平行比對，因此查詢時間主要是單張上傳圖片的模型推論加上毫秒級向量搜尋。
