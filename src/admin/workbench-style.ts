export const workbenchStyle = `
:root { color-scheme: dark; color: #d9dce1; background: #202124; font-size: 14px; }
body { background: #202124; color: #d9dce1; }
.shell { display: block; }
.sidebar { position: static; height: 64px; padding: 0 30px; display: flex; flex-direction: row; align-items: center; justify-content: space-between; background: #202124; border: 0; border-bottom: 1px solid #3a3d42; gap: 20px; }
.brand { padding: 0; } .brand strong { font-size: 19px; } .brand small { color: #abb1bb; font-size: 13px; }
.brand-mark, .nav-icon, .nav-label, .side-bottom, .eyebrow { display: none; }
.nav { display: flex; height: 100%; gap: 26px; }
.nav button { width: auto; padding: 0 2px; border-radius: 0; color: #abb1bb; border-bottom: 3px solid transparent; font-size: 14px; }
.nav button:hover { background: transparent; color: #fff; }
.nav button[aria-current=page] { background: transparent; color: #f1f3f5; border-bottom-color: #75a7d5; }
main { padding: 26px 30px 60px; } .content { max-width: none; }
.topline { margin-bottom: 22px; } h1 { font-size: 24px; letter-spacing: 0; }
.button { background: transparent; color: #c8d4e2; border-color: #505965; border-radius: 4px; font-weight: 500; }
.button:hover { background: #333941; } .button.primary { background: #354e66; border-color: #577b9b; color: #eef5fc; } .button.primary:hover { background: #40617e; }
.button.danger { color: #e5a59c; border-color: #80544e; }
.card, .stat, .notice, .empty, .load-error { background: transparent; border-radius: 4px; box-shadow: none; color: #b9c0ca; border-color: #3b4048; }
.card { padding: 20px; } .stat strong, .notice strong, td strong { color: #e0e4ea; }
.card-head p, .card-detail, .hint, .message, .refresh-note, .intro p, .session-row small { color: #b1b8c3; }
.notice.warning { background: #302c24; border-color: #6c572f; }
.state { background: #30343a; color: #c5ccd5; border-radius: 4px; }
.state[data-state=connected] { background: #25392e; color: #a3d4b0; }
.state[data-state=error], .message[data-kind=error], .load-error { color: #edaaa1; }
.state[data-state=connecting] { background: #3a3122; color: #edc16f; }
.field label, .subhead, .session-state { color: #c2c9d2; }
.field input, .field select, .field textarea, .filters input, .filters select { background: #25272b; color: #e0e4ea; border-color: #4d535c; border-radius: 4px; }
.field input::placeholder { color: #9fa7b3; }
.footer { position: fixed; bottom: 0; left: 0; right: 0; margin: 0; padding: 12px 30px; background: #202124; border-top-color: #3b4048; color: #adb6c2; font-size: 12px; z-index: 2; }
.footer a { color: #b7c9dc; } .service-mark { display: none; }
.workbench { display: grid; grid-template-columns: minmax(300px,32%) minmax(0,1fr); min-height: calc(100vh - 195px); gap: 0; }
.conversation-pane { padding-right: 24px; border-right: 1px solid #3a3d42; min-width: 0; }
.conversation-pane .filters { display: grid; gap: 12px; margin-top: 0; }
.conversation-pane .filters input, .conversation-pane .filters select { width: 100%; }
.conversation-pane .field { gap: 8px; }
.conversation-pane #session-match-count { margin: 12px 0; display: block; }
.conversation-pane #sessions-table { margin-top: 16px; }
.conversation { width: 100%; display: block; text-align: left; padding: 18px 14px; color: #dce1e8; border: 0; border-bottom: 1px solid #3a3d42; border-left: 3px solid transparent; border-radius: 0; background: transparent; cursor: pointer; }
.conversation:hover { background: #292d33; } .conversation[aria-pressed=true] { background: #354353; border-left-color: #80aed9; }
.conversation strong { font-size: 15px; font-weight: 600; display: block; margin-bottom: 10px; overflow-wrap: anywhere; }
.conversation .row-meta { display: flex; justify-content: space-between; gap: 8px; color: #b2bcc9; font-size: 12px; }
.detail-pane { min-width: 0; padding-left: 38px; }
#session-detail { border: 0; padding: 0; margin-top: 0; }
#session-detail-title { font-size: 25px; margin: 0; }
#session-detail .notice { border: 0; border-top: 1px solid #3b4048; border-radius: 0; padding: 20px 0; }
#session-detail-content p { overflow-wrap: anywhere; }
#session-detail-content .notice { font-size: 14px; line-height: 1.8; }
#session-detail-content .notice.warning { padding: 18px 20px; border: 1px solid #6c572f; }
#session-detail-content .card-detail { padding-bottom: 18px; border-bottom: 1px solid #3b4048; }
.detail-placeholder { padding: 42px 0; color: #aab4c0; line-height: 1.8; }
#page-sessions > .section { display: none; }
.session-state[data-status=waiting_for_input], .session-state[data-status=waiting_for_approval], .session-state[data-status=running] { color: #edc16f; }
.session-state[data-status=completed] { color: #9ccbaf; } .session-state[data-status=failed] { color: #edaaa1; }
@media(max-width:850px) { .sidebar { height: auto; min-height: 64px; flex-wrap: wrap; padding: 16px; } .nav { gap: 20px; height: 40px; } main { padding: 20px 16px 80px; } .workbench { grid-template-columns: 270px minmax(0,1fr); } .detail-pane { padding-left: 20px; } .footer { padding: 12px 16px; } }
@media(max-width:680px) { .workbench { grid-template-columns: 1fr; } .conversation-pane { border: 0; padding: 0; } .detail-pane { border-top: 1px solid #3a3d42; padding: 24px 0; margin-top: 24px; } .nav { overflow-x: auto; } .brand small { display: none; } .field-grid > .field:not(.full) > label { min-height: 0; } .footer { position: static; flex-wrap: wrap; gap: 4px; } }
`;
