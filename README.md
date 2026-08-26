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

## 執行

發佈資料夾執行 `P_QA.Web.exe`，瀏覽器開啟 `http://localhost:5220`。

IIS 部署時使用 `No Managed Code`、x64 應用程式集區，網站實體路徑指向 publish 資料夾。`connections.aes` 與 `connections.key` 必須成對部署並限制讀取權限。

## NAS 設定

附件根目錄為 `\\nasts469\東正舊系統`。若 IIS 應用程式集區帳號本身沒有 NAS 權限，停止網站後執行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Tools\Set-QaNasCredentials.ps1
```

設定完成後重新啟動網站。
