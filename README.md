# B站缓存合并工具 v1.4

---

## 作用

把 B 站客户端/电视端下载的缓存目录（每个 `c_*` 目录里的 `video.m4s` + `audio.m4s`）无损封装成普通 mp4，并按 `entry.json` 里的标题命名。

不需要重新编码，只做封装（`-c copy`）

---

## 运行

双击 `BiliMerge.exe` 即可，绿色单文件，不装任何运行时。  
（依赖系统自带的 .NET Framework 4.x，Win10/11 默认就有。）

---

## 使用步骤

1. 程序启动会自动查找 ffmpeg，顶部绿字显示版本和路径。
   - 找不到时显示红字，点「下载 FFmpeg」会自动下载并解压到本程序目录下的 `ffmpeg` 文件夹；
   - 或者点「指定路径」手工选 `ffmpeg.exe`。

2. 「输入目录」选缓存根目录，例如：

   ```text
   C:\ffmpeg\tv.danmaku.bili\download
   ```

   程序会自动向下找所有 `c_*` 子目录，无需手动一个个指。

3. 「输出目录」选成品存放位置，默认是本程序目录下的 `output`。

4. 点「扫描」，列表会列出所有缓存条目（标题 / 分P / 大小），默认全部勾选。

5. 取消勾选不需要的条目，只处理勾上的：
   - 每行最左边是复选框，点一下切换勾选；
   - 「全选」一键全勾，「全不选」一键全不勾；
   - 右上角实时显示「已勾选 N / 总数」，一个都没勾时点合并会被拦下；
   - 勾选框可以部分勾选，比如只勾第 1、5、9 集。

6. 点「开始合并」。进度条、每行状态、下方日志都会实时更新。
   - 已存在的同名文件会自动跳过，所以中断后重跑不会重复劳动。

7. 完成后点「打开输出目录」直接查看。

**注意事项：** 序号按整库顺序固定，取消勾选不会让后面的序号前移。  
比如只勾第 1、3 条，产出仍然是 `_001` 和 `_003`，`_002` 空着。  
这样不同批次的结果放在一起也不会重名或错位。

---

## 文件命名

```text
标题_序号.mp4
```

序号按 `avid + cid` 排序后全局连续编号（三位补零，如 `001`、`002`），  
所以同名的多集视频不会互相覆盖，文件管理器里也天然按顺序排列。  
标题中的 Windows 非法字符会被替换成空格，超长自动截断到 120 字。

---

## 编码兼容

B 站现在有 HEVC 和 AV1 两种编码，程序会先用 `ffprobe` 探测实际编码，  
再打对应的 mp4 标签（`hvc1` / `av01` / `avc1` / `vp09`）。  
标签不对会导致 QuickTime、电视盒子等播放器无法识别，这一步不能省。

---

## 没有音轨的情况

部分稿件（纯伴奏、学唱类）本来就没有 `audio.m4s`，程序会自动只封装视频轨，  
不是出错。

---

## 配置保存

上次使用的输入/输出目录、指定的 ffmpeg 路径会记在：

```text
%APPDATA%\BiliMerge\settings.json
```

删掉它即可恢复默认。

---

## 命令行模式（可选，便于批处理）

```text
BiliMerge.exe --run "输入目录" "输出目录" 数量
BiliMerge.exe --selftest "输入目录"
```

`--run` 不弹窗，直接把结果写到程序目录的 `headless.txt`。

---

## 源码

同目录的 `BiliMerge.cs`，用 .NET Framework 自带的 `csc` 编译即可：

```bat
csc /target:winexe /codepage:65001 ^
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
    /reference:System.Web.Extensions.dll ^
    /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
    /out:BiliMerge.exe BiliMerge.cs
```