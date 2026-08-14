# Minetake VPM Repository

公開URL:

`https://minetake01.github.io/vpm-repos/vpm.json`

## パッケージ

- Folder Alias

Unity 2022.3.22f1の標準Rename操作から、`Assets` 配下のフォルダについて表示名だけを変えるか、実フォルダ名を変えるか選択できるVRChat向けEditorパッケージです。表示名が設定されたフォルダは黄色で表示され、Projectウィンドウ内では表示名を優先して自然順に並びます。

### developブランチ限定

- Hand Tracking Mode

選択したアバターから、AnimationとVRChatの指トラッキングを手動で切り替えるModular Avatar prefabを生成する開発中のEditorパッケージです。公開用の `main` ブランチとVPM listingには含めません。

## 公開手順

1. GitHubに `minetake01/vpm-repos` としてpushします。
2. Repository SettingsのPagesで、Sourceを `GitHub Actions` に設定します。
3. `<package-name>-<version>` タグ（例: `net.minetake.folder-alias-1.0.0`）をpushします。
4. ReleaseワークフローがVPM用ZIPをGitHub Releaseへ追加し、Pagesワークフローが `vpm.json` を公開します。

新しいバージョンでは、パッケージ側の `package.json` とルートの `vpm.json` を同時に更新してください。
