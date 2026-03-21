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
