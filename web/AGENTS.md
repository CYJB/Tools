# web/ — Web 工具

基于 React 19 + TypeScript + Vite 的前端工具集，部署到 GitHub Pages。

## 技术选型

- **UI 组件库**: [Shineout](https://sheinsight.github.io/shineout-next/)（`shineout`）
- **路由**: react-router-dom v6，使用 HashRouter
- **构建**: Vite
- **CI/CD**: `.github/workflows/deploy-web.yml`，push 到 main 时自动构建并部署到 gh-pages 分支

## 本地开发

```bash
npm install
npm start
```

## 目录结构

```
web/
├── index.html
├── package.json
├── vite.config.ts
├── tsconfig.json
├── public/
│   └── favicon.ico
└── src/
    ├── App.tsx              # 路由入口
    ├── index.tsx            # 应用挂载
    ├── index.css            # 全局样式
    ├── pages/
    │   ├── Home.tsx/.css    # 首页（工具卡片列表）
    │   ├── Lyric.tsx/.css   # LRC 歌词合并
    │   └── Japanese.tsx/.css # JIS 日文假名键盘
    └── utils/
        └── lrcParser.ts     # LRC 歌词解析/合并/序列化
```

## 页面列表

| 路由 | 组件 | 说明 |
|:----:|:----:|------|
| `#/` | `Home` | 首页，展示工具卡片，点击跳转各工具页面 |
| `#/lyric` | `Lyric` | 多语言 LRC 歌词合并，支持拖拽上传、自动匹配、手动修正 |
| `#/japanese` | `Japanese` | JIS 假名键盘，支持平假名/片假名切换、浊点合字 |

## 添加新页面

1. 在 `src/pages/` 下新建 `XxxPage.tsx` 和 `XxxPage.css`
2. 在 `src/App.tsx` 中添加 `<Route>` 路由
3. 在 `src/pages/Home.tsx` 的 `TOOLS` 数组中添加卡片入口
