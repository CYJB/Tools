## 压缩视频

ffmpeg -i video.mp4 -s 1920x1080 output.mp4
ffmpeg -i video.mp4 -s 720x1280 -b:V 2000K output.mp4
ffmpeg -i video.mp4 -s 720x720 -b:V 2000K output.mp4
ffmpeg -i video.mp4 -s 1440x720 -b:V 2000K output.mp4


1080 × 1440
ffmpeg -i "sp (1).mp4" -s 720x960 -b:V 2000K output.mp4

1080 × 810
ffmpeg -i "sp (2).mp4" -s 960x720 -b:V 2000K output2.mp4

## 裁剪尺寸

~/Downloads/ffmpeg -i video.mp4 -strict -2 -vf crop=1280:720 -b:V 2000K out.mp4

~/Downloads/ffmpeg -i v1.mp4 -strict -2 -vf crop=540:960 -b:V 2000K out.mp4

crop=width:height:x:y，其中width 和height 表示裁剪后的尺寸，x:y 表示裁剪区域的左上角坐标。

## 合并视频

ffmpeg -f concat -safe 0 -i list.txt -c copy concat.mp4
在list.txt文件中，对要合并的视频片段进行了描述。
内容如下

```
file split.mp4
file split1.mp4
```

## 按时间裁剪

从 10s 开始裁剪 30s

ffmpeg -ss 00:10 -t 30 -i input.mp4 -c copy out.mp4
ffmpeg -ss 00:10 -to 00:20 -i input.mp4 -c copy out.mp4
ffmpeg -t 235 -i video.mp4 -c copy out.mp4

## 旋转

./ffmpeg -i input -s 1280x720 -b:V 2000K -vf crop=720:400 "t.mp4"

./ffmpeg -i success.mp4 -metadata:s:v rotate="90" -codec copy output_success.mp4
./ffmpeg -i test.mp4 -vf "transpose=2" out.mp4

顺时针旋转画面90度 ffmpeg -i test.mp4 -vf "transpose=1" out.mp4 
逆时针旋转画面90度 ffmpeg -i test.mp4 -vf "transpose=2" out.mp4 
顺时针旋转画面90度再水平翻转 ffmpeg -i test.mp4 -vf "transpose=3" out.mp4 
逆时针旋转画面90度水平翻转 ffmpeg -i test.mp4 -vf "transpose=0" out.mp4

## 提取关键帧

ffmpeg -i video_name.mp4 -vf select='eq(pict_type\,I)' -vsync 2 -s 1920*1080 -f image2 keyframe-%02d.jpeg

-vf:是一个命令行，表示过滤图形的描述。选择过滤器select会选择帧进行输出：pict_type和对应的类型:PICT_TYPE_I 表示是I帧，即关键帧；
-vsync 2:阻止每个关键帧产生多余的拷贝；
-f image2 name_%02d.jpeg:将视频帧写入到图片中，样式的格式一般是: “%d” 或者 “%0Nd”

ffprobe -loglevel error -skip_frame nokey -select_streams v:0 -show_entries frame=pkt_pts_time -of csv=print_section=0 input.mp4

### 按照关键帧分割视频

ffmpeg -i INPUT.mp4 -acodec copy -f segment -vcodec copy -reset_timestamps 1 -map 0 OUTPUT％d.mp4

echo "file b_2_1.mp4" > list.txt
echo "file b_2_2.mp4" >> list.txt
echo "file b_2_3.mp4" >> list.txt
../../ffmpeg -f concat -i list.txt -c copy concat.mp4

### 提取音频

ffmpeg -i input.mp4 -vn -codec:a libmp3lame -q:a 4 output.mp3

参数解释如下：

-i input.mp4: input.mp4 是输入视频文件。
-vn: 表示不包含视频流（即仅提取音频）。
-codec:a libmp3lame: 指定输出音频编码器为 libmp3lame，这是 MP3 编码。
-q:a 4: 音频质量设置，范围从 0（最好）到 5（最差），通常使用范围在 0 到 9。
output.mp3: 输出文件的名称，这里是转换后的 MP3 文件。

### 音频格式转换

ffmpeg -i input.flac -ab 320k -map_metadata 0 -id3v2_version 3 output.mp3

## m4b 有声书

`audiobook.cs` 用到的命令都在这里，重点是那几个**退出码为 0 但结果是错的**坑。

### 合并成带章节的 m4b

章节表用 ffmetadata 格式写成文件，作为第二个输入：

```
;FFMETADATA1
title=书名
[CHAPTER]
TIMEBASE=1/1000
START=0
END=5000
title=第一章
```

TIMEBASE=1/1000 表示 START/END 是毫秒。

```
ffmpeg -f concat -safe 0 -i list.txt -f ffmetadata -i meta.txt -map 0:a \
  -map_chapters 1 -map_metadata 1 -c:a aac -b:a 64k \
  -metadata media_type=2 -brand "M4B " -movflags +faststart -f mp4 out.m4b
```

- `-map_chapters 1` 不能省：省略时 ffmpeg 取「第一个有章节的输入」，改章节时旧的会被静默保留。`-map_metadata` 是另一个独立开关。清空章节用 `-map_chapters -1`。
- `-brand "M4B "` 末尾的空格是关键，必须 4 个字符。写成 `M4B` 会被静默忽略并退回 `isom`。用 `ProcessStartInfo.ArgumentList` 传参才保得住这个空格。
- `-metadata media_type=2` 写入 `stik` atom 标记为有声书。不传的话文件里根本没有 `stik`，Apple Books 不提供断点续听。
- concat demuxer **不会**自动带出章节（章节表不是流），必须自己写。list.txt 的头必须是 `ffconcat version 1.0`（带空格），路径用单引号包裹，内部的单引号写成 `'\''`。

### 无损改元数据和章节

```
ffmpeg -i in.m4b -f ffmetadata -i meta.txt -map 0:a -map 0:v? \
  -map_chapters 1 -map_metadata 1 -c copy \
  -brand "M4B " -metadata media_type=2 -movflags +faststart -f mp4 out.m4b
```

- 不能用 `-map 0`：原有的章节轨是一条 `codec_type=data` 的普通流，会被一起带上，和新章节表叠加成两条。`-map 0:a -map 0:v?` 显式列举（`?` 表示没有视频流时不报错）。
- `-c copy` 重封装会丢掉 `major_brand`（它是 muxer 选项、不走 metadata 映射），必须重新传 `-brand "M4B "`。`media_type` 会随全局 metadata 保留，但也建议显式传。
- 音频是逐位不变的，可以用 `ffmpeg -i f.m4b -map 0:a -c copy -f md5 -` 前后比对验证。

### 封面

```
ffmpeg -i in.m4b -i cover.jpg -map 0:a -map 2:v -c copy \
  -disposition:v:0 attached_pic -f mp4 out.m4b
```

JPEG 和 PNG 都能 `-c:v copy`。换封面时不要再用 `-map 0:v`，否则会有两张。

### concat 的输入一致性

安全规则：**concat demuxer 只在 codec、采样率、声道全部一致时才用**，其余情况一律走 filter_complex。下面每一条都是实测出来的，且失败时退出码都是 0。

- 全部一致且已经是 AAC → `-c:a copy`，无损且几秒完成。源是 `.aac`（ADTS）时要加 `-bsf:a aac_adtstoasc`。
- **codec 不同** → 走 demuxer 会静默丢帧，日志刷 `PTS ... invalid dropping`。改用 `-filter_complex "[0:a][1:a]concat=n=2:v=0:a=1[aout]"`。
- **采样率不同 + AAC(m4a) 源** → 走 demuxer 即使重编码也会**静默丢掉约一半音频**（实测 6 秒素材只出 3 秒）。mp3 源在同样条件下看似正常（ffmpeg 自动插 aresample），所以这个坑只对 AAC 暴露——别把 mp3 的测试结果推广到 m4a。
- **混合采样率走 `-c:a copy`** → 静默损坏，48kHz 的采样按 44.1kHz 解释，约 9% 变调，而时长显示还正常。
- 源 mp3 带封面时有 2 条流，会让 demuxer 的流匹配错位，也要走 filter_complex。
- mp3 直接 `-c:a copy` 进 mp4 会硬失败（`Could not find tag for codec mp3`），这是好事。

### 一定要显式固定 -ar / -ac

不固定时 ffmpeg 按**第一个输入**协商参数，于是「哪个文件排在前面」会决定整本书的音质：

```
源 a.mp3 = 22050Hz/mono，b.mp3 = 44100Hz/stereo，a 排在前面
不固定  → 输出 22050Hz/mono（全书被降采样 + downmix）
-ar 44100 -ac 2 → 输出 44100Hz/stereo，日志只有升采样，没有降采样
```

`concat` **滤镜**有同样的问题，而且更隐蔽：它会先把后面的输入降到第一个输入的参数、拼完再升回输出参数。`-v verbose` 下能看到：

```
[auto_aresample_0] ch:2 chl:stereo fmt:fltp r:44100Hz -> ch:1 chl:mono fmt:fltp r:22050Hz
[auto_aresample_1] ch:1 chl:mono   fmt:fltp r:22050Hz -> ch:2 chl:stereo fmt:fltp r:44100Hz
```

11kHz 以上永久丢失、立体声塌成单声道，而输出仍宣称 44100/stereo。修法是每个输入先显式归一化再拼接：

```
-filter_complex "[0:a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo[a0];
                 [1:a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo[a1];
                 [a0][a1]concat=n=2:v=0:a=1[aout]"
```

### AAC 码率的实际上限

ffmpeg 原生 aac 编码器会自己饱和，请求再高也没用（实测 1 秒 sine）：

| 请求 | 单声道实际 | 立体声实际 |
|-----|----------|----------|
| 128k | 116k | 128k |
| 160k | 135k | 160k |
| 256k | 133k | 215k |
| 320k | 133k | 208k |
| 512k | 133k | 208k |

所以「对齐源 mp3 的 320k」对单声道内容其实拿不到 320k，但也够用——133k 的单声道 AAC-LC 对语音是透明的。

### 取源码率的坑

`stream.bit_rate` 和 `format.bit_rate` 各有失效场景，要 stream 优先、format 兜底：

| 源 | stream.bit_rate | format.bit_rate |
|----|----------------|-----------------|
| flac | **N/A** | 准确 |
| wav | 准确 | 准确 |
| mp3 | 准确 | 虚高约 1.4%（含标签开销） |
| m4a | 准确 | 虚高约 4%（含容器开销） |

### 无损源能不能不转码

技术上都可以，但只有 ALAC 靠谱：

- flac → `-c:a alac`：**真无损**，实测解码后的 PCM 与源 MD5 完全一致，brand/stik 也正常。代价是体积比 flac 大约 25%。
- flac → `-c:a copy`：能进 mp4（tag `fLaC`），但 Apple 播放器基本不认。
- wav/PCM → `-c:a copy`：同样能进 mp4，同样不看好。

`audiobook.cs` 目前的策略是无损源也编 AAC 并提示用户备份原文件，没有走 ALAC——因为「ALAC + stik=2 + 章节轨」这条有声书路径能否被 Apple Books 正常当有声书处理，没有设备可验证。


### 章节边界的漂移

按 `ffprobe -show_entries format=duration` 累加算边界，在源文件**缺少 Xing/VBRI 头**时每个文件会差约 26ms 并且逐章累积（100 多个文件能差出几秒）。ffprobe 在 `-v warning` 下会提示：

```
[mp3 @ 0x...] Estimating duration from bitrate, this may be inaccurate
```

看到这句就改用完整解码得到的时长：

```
ffmpeg -v error -progress pipe:1 -i in.mp3 -map 0:a -f null -
```

取 stdout 里最后一个 `out_time_us=`。CBR 或带 Xing 头时容器时长精确到 0.1ms 以内，不用付这个代价。

AAC 编码还有一帧 priming（1024 采样，44.1kHz 下约 23ms），是恒定偏移、不累积，章节点通常落在静音处，不用管。

### 探测

```
ffprobe -v error -print_format json -show_format -show_streams -show_chapters in.m4b
```

- 章节轨在 ffprobe 里是 `codec_type=data`（Nero chpl），不是 subtitle。
- `format.tags.major_brand` 应为 `"M4B "`（带末尾空格），`format.tags.media_type` 应为 `"2"`（这就是 stik）。
- **`format.duration` 是所有流的最大值，会被章节轨撑大**，验证输出时要用音频流自己的 `duration`。
- 不要用 `ffmpeg -i in.m4b -f ffmetadata out.txt` 导出章节给用户编辑：它吐 `TIMEBASE=1/10000000`（100 纳秒单位）、把末章 END 扩到总时长，还会带出 `major_brand` / `minor_version` / `compatible_brands` / `encoder` 这些 muxer 内部键，原样喂回去会变成字面的元数据标签。

