# Hand Tracking Mode

独自の指アニメーションを使用するVRChatアバターから、Animationと指トラッキングを手動で切り替えるModular Avatar prefabを生成するEditorパッケージです。

生成したprefabには独自コンポーネントを含めません。NDMFプラグインによるビルド時処理も行いません。

## 生成方法

1. Hierarchyで対象アバター、またはその子GameObjectを選択します。
2. `GameObject > VRChat > Generate Hand Tracking Mode Prefab` を実行します。
3. `Assets`以下からprefabの保存先を選択します。
4. 生成されたprefabを対象アバターの子へ配置します。
5. 必要に応じてprefabの `MA Menu Installer` からメニューの追加先を変更します。

既存アセットと同名のprefabや生成物フォルダーは上書きしません。生成し直す場合は、不要になった生成物を確認したうえで削除するか、別名で生成してください。

## 生成物

prefabは以下のModular Avatarコンポーネントだけで構成されます。

- `MA Merge Animator`
- `MA Parameters`
- `MA Menu Item`
- `MA Menu Installer`

同時に、トグル制御用Animator Controllerと、必要な場合だけ既存Playable LayerのController複製を生成します。

`MA Menu Installer` の `Install Target` が未設定なら、トグルはExpression Menuのトップへ追加されます。

## 動作

- OFF: 左右の指を `Animation` にします。
- ON: 左右の指を `Tracking` にします。

トグル用パラメーターは `HandTrackingMode` です。同期されますが保存されず、アバター読み込み時はOFFです。

## Controllerの処理

生成時に、選択した `VRCAvatarDescriptor` が直接参照している標準 `AnimatorController` だけを確認します。

公式 `VRCAnimatorTrackingControl` が左右のFingersを制御しているControllerは複製し、複製上のFingersだけを `No Change` にします。Head、Hands、Feet、Eyes、Mouthなど、指以外の設定は変更しません。元のControllerは変更しません。

処理したControllerは `MA Merge Animator` のReplaceとしてprefabへ追加されます。指用Tracking ControlがないControllerは複製しません。

## 対象範囲

本ツールだけが指のTracking Controlへ追加介入する構成を対象とします。

以下は解析も検証も行わず、そのまま対象外とします。

- Descriptorから直接確認できないController
- 標準 `AnimatorController` 以外のController
- 他のMA、VRCFury、NDMFプラグインなどが追加・変更するController
- Missing Scriptや独自StateMachineBehaviourの動作
- 生成後に行われたController構成の変更

Expression Parameters、メニュー、Animator LayerなどのVRChat上限は、本ツール独自の検査を行いません。通常のVRChat SDKおよびModular Avatarの検証結果に従います。

元Controllerや指制御の構成を変更した場合は、必要に応じてprefabを再生成してください。
