namespace ChatAgentBridge_Desktop.Onboarding;

// Keep this version aligned with OnboardingConsentStore.CurrentPolicyVersion.
public static class PolicyText
{
    public const string Version = ChatAgentBridge.Core.OnboardingConsentStore.CurrentPolicyVersion;
    private const string JapaneseTerms = """
        Chat Agent Bridge 利用規約（開発プレビュー）
        文書バージョン：2026-10-20.1
        提供者：mmiyaji
        お問い合わせ：mail@ruhenheim.org

        1. 本アプリについて
        本アプリは、利用者が管理するWindows PC上のAIエージェントCLIを、SlackまたはMattermostから操作するための非公式ツールです。OpenAI、Anthropic、Slack、Mattermost等の公式製品ではありません。各外部サービスの契約・規約も適用されます。

        2. 現状有姿・無保証
        本アプリは現状有姿で提供します。適用法令で認められる範囲で、動作、可用性、継続提供、特定目的への適合性、AIの出力の正確性・完全性、セキュリティ、データ保持、第三者の権利を侵害しないことについて、明示・黙示を問わず保証しません。中断・不具合・外部サービスやCLIの変更により利用できなくなることがあります。

        3. 利用者による管理
        利用者は、作業フォルダー、アクセス権、許可ユーザー、接続先、承認方法、結果共有先を確認してください。AIがファイルを変更・削除し、コマンドやネットワーク処理を実行する場合があります。必要なバックアップを取り、依頼と結果を確認してください。自動承認・フルアクセス・自動共有を選ぶ場合は、確認を省略する範囲を理解したうえで設定してください。利用権限のないアカウント・データ・PCには使用しないでください。

        4. 費用と外部サービス
        AI・チャットサービス、通信等の費用や利用制限は利用者の契約に従います。CLIチェックは起動・バージョン・モデル候補の確認であり、AIの実行成功、認証、利用権限、料金を保証するものではありません。

        5. 責任の範囲
        適用法令で認められる範囲で、本アプリの利用に伴う損失や損害について提供者は責任を負いません。ただし、本規約は、故意・重大な過失等に関する責任その他の法令上免除・制限できない責任を免除・制限するものではありません。利用者が法令上有する権利を排除しません。

        6. 利用の終了・文面の確認
        同意しない場合は初期設定を完了せず、アプリを終了してください。利用を終了する場合はチャット接続とWindows起動設定を解除してください。本規約とプライバシーポリシーは概要画面から再確認できます。お問い合わせは、提供者 mmiyaji（mail@ruhenheim.org）へご連絡ください。
        """;

    private const string JapanesePrivacy = """
        Chat Agent Bridge プライバシーポリシー（開発プレビュー）
        文書バージョン：2026-10-20.1
        提供者：mmiyaji
        お問い合わせ：mail@ruhenheim.org

        1. 取り扱う情報と目的
        本アプリは、利用者が設定したチャットとローカルCLIを接続し、依頼の実行、会話の継続、アクセス制御、状態確認を行うために情報を取り扱います。依頼文、実行結果、アカウント・チャンネル・スレッドID、作業フォルダー、モデル等の設定、セッション識別子、実行状況・使用量等を扱います。

        2. このPCへの保存
        接続・実行設定、セッションの関連付け、実行履歴・監査情報、同意した文書のバージョンと日時を、このWindowsユーザーのアプリデータフォルダーに保存します。接続トークンはWindows DPAPIで暗号化して保存します。ブリッジの履歴・監査ファイルには通常、依頼本文・結果本文を保存しませんが、作業パスや識別子等は含まれます。CLI接続の登録後、会話の引き継ぎ用に、直近3回までの成功した依頼・結果の抜粋をWindows DPAPIで暗号化して別ファイルに保存します。保管する会話は最大200件で、各メモは最大8,000文字です。「要約を引き継ぐ」を選んだ場合だけ、切り替え先CLIへの次の依頼にメモを添えます。本文は画面表示や処理のためメモリーでも扱い、別途CLIや外部サービスに保存されることがあります。暗号化はPC管理者や侵害されたPCからの保護を保証するものではありません。

        3. 外部との通信と共有範囲
        Slackまたは設定したMattermostサーバーとの間で、依頼・応答・接続状態等を送受信します。実行時には依頼文と作業に必要な情報を選択したCLIへ渡します。CLIは、その設定と提供者の方針に従い、AIサービス等へデータを送信する場合があります。ローカルPCでCLIを起動することは、AI処理やデータ保存がすべてPC内で完結することを意味しません。各外部サービスのプライバシーポリシー・契約も確認してください。

        チャットへ共有する結果や操作承認の詳細は、そのチャンネル・スレッドを閲覧できる人に見えます。許可ユーザーの設定は、チャット側での投稿の閲覧範囲を制限するものではありません。接続・切断時には本文を含まない状態通知を送る場合があります。

        4. 提供者への送信
        この開発プレビューには、ブリッジ独自の解析・広告サービスへの利用情報や会話本文の自動送信を組み込んでいません。OS、配布ストア、CLI、チャット・AIサービスがそれぞれ行う情報の取扱いは、各提供者の方針に従います。利用者が問い合わせ等で情報を送る場合は、必要な範囲に絞り、トークン等を除いてください。

        5. 保存期間・削除・利用の終了
        保存データは、利用者が削除するまでPCに残る場合があります。アプリを終了し、概要画面に表示される設定保存先のデータを削除することでブリッジ側の保存情報を削除できます。アンインストール時の扱いはWindowsの配布形態によります。ブリッジのデータを削除しても、チャット・AIサービスやCLI側の会話履歴は削除されません。それぞれのサービス・CLIで管理してください。

        6. お問い合わせ
        本アプリのデータの取扱いに関するお問い合わせは、提供者 mmiyaji（mail@ruhenheim.org）へご連絡ください。公開チャットや公開の問い合わせ欄に、トークン、依頼・結果の機密情報を掲載しないでください。
        """;

    public static string Terms => ChatAgentBridge.Core.Localization.AppLanguage.IsJapanese ? JapaneseTerms : EnglishTerms;
    public static string Privacy => ChatAgentBridge.Core.Localization.AppLanguage.IsJapanese ? JapanesePrivacy : EnglishPrivacy;

    private const string EnglishTerms = """
        Chat Agent Bridge Terms of Use (Development Preview)
        Document version: 2026-10-20.1
        Provider: mmiyaji
        Contact: mail@ruhenheim.org

        1. About this application
        This unofficial tool lets you operate an AI agent CLI on a Windows PC you control through Slack or Mattermost. It is not an official product of OpenAI, Anthropic, Slack, Mattermost, or any other service. The terms and policies of each external service also apply.

        2. As-is; no warranties
        The application is provided "as is." To the extent permitted by applicable law, no express or implied warranties are made regarding operation, availability, continued provision, fitness for a particular purpose, accuracy or completeness of AI output, security, data retention, or non-infringement of third-party rights. Interruptions, defects, or changes to external services or CLIs may make the application unavailable.

        3. User responsibility
        Check your working folders, permissions, authorized users, connection targets, approval method, and result-sharing destination. AI may change or delete files and run commands or network operations. Keep necessary backups and review requests and results. If you choose automatic approval, full access, or automatic sharing, understand what review those settings skip. Do not use the application with accounts, data, or PCs you are not authorized to access.

        4. Costs and external services
        Costs and usage limits for AI and chat services, communications, and other services are governed by your agreements with those providers. The CLI check verifies startup, version, and model options; it does not guarantee successful AI execution, authentication, usage rights, or pricing.

        5. Limitation of liability
        To the extent permitted by applicable law, the provider is not liable for losses or damages arising from use of this application. These terms do not exclude or limit liability for intentional acts, gross negligence, or other liability that cannot legally be excluded or limited. They do not remove rights you have under applicable law.

        6. Ending use and reviewing these terms
        If you do not agree, do not complete setup and close the application. To stop using it, disconnect chat connections and turn off the Windows startup setting. You can review these terms and the Privacy Policy again from the Overview screen. For questions, contact provider mmiyaji at mail@ruhenheim.org.
        """;

    private const string EnglishPrivacy = """
        Chat Agent Bridge Privacy Policy (Development Preview)
        Document version: 2026-10-20.1
        Provider: mmiyaji
        Contact: mail@ruhenheim.org

        1. Information handled and purposes
        The application connects chat services configured by you with a local CLI to run requests, continue conversations, control access, and show status. It handles request text, execution results, account, channel and thread IDs, settings such as working folders and models, session identifiers, execution status, and usage information.

        2. Storage on this PC
        Connection and execution settings, session associations, execution history and audit information, and the version and date of documents you accepted are stored in this Windows user's application data folder. Connection tokens are encrypted with Windows DPAPI. Bridge history and audit files ordinarily do not store request or result text, but can include working paths and identifiers. After you register a CLI connection, excerpts of up to three recent successful requests and results are saved in a separate file encrypted with Windows DPAPI. At most 200 conversation notes are retained, each up to 8,000 characters. A note is attached to the next request to the target CLI only when you select summary transfer. Text is also handled in memory for display and processing, and may also be stored separately by a CLI or external service. Encryption does not guarantee protection from a PC administrator or a compromised PC.

        3. External communications and sharing
        The application exchanges requests, responses, and connection status with Slack or the Mattermost server you configure. During execution, it passes the request and information needed for the task to the selected CLI. Depending on its configuration and provider policies, the CLI may send data to AI services. Running a CLI on a local PC does not mean that all AI processing or data storage happens on that PC. Review the privacy policies and agreements of each external service.

        Results shared to chat and details of operation approvals are visible to people who can view that channel or thread. The authorized-user setting does not limit who can view posts on the chat service. Connection and disconnection notices that do not include message text may also be sent.

        4. Information sent to the provider
        This development preview does not include automatic sending of usage information or conversation text to a bridge-operated analytics or advertising service. Information handling by the OS, distribution store, CLI, chat, and AI services is governed by each provider's policies. If you send information to the provider, for example in a support request, limit it to what is needed and remove tokens and secrets.

        5. Retention, deletion, and stopping use
        Stored data may remain on the PC until you delete it. You can delete bridge-stored information by closing the application and deleting the data in the settings storage location shown on the Overview screen. Uninstallation behavior depends on the Windows distribution method. Deleting bridge data does not delete conversation history held by chat or AI services or the CLI; manage that history in each service or CLI.

        6. Contact
        For questions about this application's handling of data, contact provider mmiyaji at mail@ruhenheim.org. Do not post tokens or confidential request or result information in public chats or public support fields.
        """;
}
