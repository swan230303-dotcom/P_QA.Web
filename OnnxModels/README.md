# P_QA.Web 圖像搜尋模型

- 檔名：`vision_model.onnx`
- 架構：OpenAI CLIP ViT-B/32 視覺編碼器
- ONNX 版本：Xenova / Transformers.js INT8 量化匯出
- 輸入：`pixel_values`，`float32[1,3,224,224]`
- 輸出：`image_embeds`，`float32[1,512]`
- 檔案大小：89,117,001 bytes
- SHA-256：`583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299`
- 來源：https://huggingface.co/Xenova/clip-vit-base-patch32/blob/main/onnx/vision_model_quantized.onnx
- 原始模型：https://huggingface.co/openai/clip-vit-base-patch32

模型檔已由 `.gitignore` 排除，避免一般 Git 提交長期累積大型二進位檔；Visual Studio / `dotnet publish` 仍會把本機存在的 ONNX 檔複製到發佈資料夾。

正式使用前應以公司自己的實物照片與官方圖片進行召回率驗證。CLIP 模型卡特別提醒，細粒度分類可能是其限制之一，因此外觀極為相近、僅型號或小字不同的商品仍應由使用者確認。
