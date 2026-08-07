var k=class extends Error{constructor(e,t){let i=[];e.title&&i.push(e.title),e.detail&&i.push(e.detail);for(let o of Object.values(e.errors??{}))for(let s of o)i.push(s);super(i.join(" \u2014 ")||`HTTP ${t}`),this.name="ApiError",this.status=t,this.problem=e,this.errors=e.errors??null,this.traceId=e.traceId??null}};async function I(n){let e=await n.json().catch(()=>({}));return new k(e,n.status)}async function u(n,{method:e="GET",body:t,signal:i}={}){let o=await fetch(n,{method:e,signal:i,headers:t!==void 0?{"Content-Type":"application/json"}:void 0,body:t!==void 0?JSON.stringify(t):void 0});if(!o.ok)throw await I(o);return o.status===204?null:o.json()}var A=`/* InteractiveReport widget theme \u2014 an APEX Universal-Theme-flavored skin.
   This sheet is bundled into each custom element's shadow root. Host-page CSS
   cannot reach it; hosts can still theme the documented --ir-* properties. */

:host {
    all: initial;
    display: block;
    --ir-accent: #0572ce;
    --ir-accent-soft: #eef5fc;
    --ir-border: #d5dbe1;
    --ir-border-light: #e8ebee;
    --ir-bg: #ffffff;
    --ir-bg-soft: #f7f8f9;
    --ir-bg-header: #f2f4f6;
    --ir-text: #1f2733;
    --ir-text-muted: #5d6771;
    --ir-danger: #b3261e;
    --ir-radius: 3px;
    --ir-font: system-ui, -apple-system, "Segoe UI", sans-serif;
    --ir-font-size: 13px;
    /* Chart palette: fixed categorical order (slot 1 doubles as the single-series
       color), validated for adjacent-slot CVD separation on the white surface.
       The below-3:1-contrast slots are relieved by the built-in data table. */
    --ir-chart-1: #0572ce;
    --ir-chart-2: #eb6834;
    --ir-chart-3: #1baf7a;
    --ir-chart-4: #eda100;
    --ir-chart-5: #e87ba4;
    --ir-chart-6: #008300;
    --ir-chart-7: #4a3aa7;
    --ir-chart-8: #e34948;
    --ir-chart-grid: var(--ir-border-light);
    --ir-chart-text: var(--ir-text-muted);
    font-family: var(--ir-font);
    font-size: var(--ir-font-size);
    line-height: normal;
    color: var(--ir-text);
    background: var(--ir-bg);
    border: 1px solid var(--ir-border);
    border-radius: 4px;
}

*, *::before, *::after { box-sizing: border-box; }
[hidden] { display: none !important; }

.ir-icon { display: inline-flex; align-items: center; line-height: 0; }

/* --- buttons & inputs ----------------------------------------------------- */

.ir-btn {
    font: inherit;
    font-size: var(--ir-font-size);
    color: var(--ir-text);
    background: var(--ir-bg);
    border: 1px solid #c3cbd3;
    border-radius: var(--ir-radius);
    padding: 4px 10px;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    gap: 5px;
    line-height: 1.35;
}
.ir-btn:hover:not(:disabled) { background: #f0f3f6; }
.ir-btn:disabled { opacity: .45; cursor: default; }
.ir-btn-primary { background: var(--ir-accent); border-color: var(--ir-accent); color: #fff; }
.ir-btn-primary:hover:not(:disabled) { background: #0468bc; }
.ir-btn-danger { background: var(--ir-danger); border-color: var(--ir-danger); }
.ir-btn-danger:hover:not(:disabled) { background: #99201a; }

.ir-input, .ir-select, .ir-textarea {
    font: inherit;
    font-size: var(--ir-font-size);
    color: var(--ir-text);
    background: var(--ir-bg);
    border: 1px solid #c3cbd3;
    border-radius: var(--ir-radius);
    padding: 4px 8px;
}
.ir-input-wide { min-width: 16rem; }
.ir-textarea { width: 100%; box-sizing: border-box; resize: vertical; font-family: ui-monospace, Consolas, monospace; }

.ir-btn:focus-visible, .ir-input:focus-visible, .ir-select:focus-visible,
.ir-textarea:focus-visible, .ir-menu-item:focus-visible, .ir-chip-label:focus-visible {
    outline: 2px solid var(--ir-accent);
    outline-offset: 1px;
}

/* --- toolbar ---------------------------------------------------------------- */

.ir-toolbar {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
    padding: 8px 10px;
    background: var(--ir-bg-soft);
    border-bottom: 1px solid var(--ir-border);
    border-radius: 4px 4px 0 0;
}

.ir-search { display: inline-flex; align-items: stretch; }
.ir-search .ir-search-scope {
    border-radius: var(--ir-radius) 0 0 var(--ir-radius);
    border-right: none;
    color: var(--ir-text-muted);
    padding: 4px 7px;
    gap: 2px;
}
.ir-search-input {
    font: inherit;
    font-size: var(--ir-font-size);
    border: 1px solid #c3cbd3;
    padding: 4px 8px;
    width: clamp(140px, 26vw, 260px);
}
.ir-search .ir-go { border-radius: 0 var(--ir-radius) var(--ir-radius) 0; border-left: none; }

.ir-viewbtns { display: inline-flex; }
.ir-viewbtn { border-radius: 0; border-right-width: 0; color: var(--ir-text-muted); padding: 4px 8px; }
.ir-viewbtn:first-child { border-radius: var(--ir-radius) 0 0 var(--ir-radius); }
.ir-viewbtn:last-child { border-radius: 0 var(--ir-radius) var(--ir-radius) 0; border-right-width: 1px; }
.ir-viewbtn.ir-active { background: var(--ir-accent-soft); color: var(--ir-accent); }

.ir-actionsbtn { font-weight: 500; }
.ir-spacer { flex: 1; }

.ir-saved { display: inline-flex; align-items: center; gap: 6px; }
.ir-saved-label { font-size: 12px; color: var(--ir-text-muted); }
.ir-saved-select, .ir-report-select { max-width: 16rem; }

/* --- busy bar ---------------------------------------------------------------- */

.ir-busybar { height: 2px; position: relative; overflow: hidden; }
.ir-busy .ir-busybar::after {
    content: "";
    position: absolute;
    top: 0; bottom: 0; left: 0;
    width: 40%;
    background: var(--ir-accent);
    animation: ir-busy-slide 1s linear infinite;
}
@keyframes ir-busy-slide {
    from { transform: translateX(-100%); }
    to { transform: translateX(350%); }
}

/* --- notices ---------------------------------------------------------------- */

.ir-notices { margin: 0 10px; }
.ir-banner {
    display: flex;
    align-items: baseline;
    gap: 10px;
    margin: 8px 0;
    padding: 7px 11px;
    border-radius: var(--ir-radius);
    font-size: 12.5px;
    border: 1px solid;
}
.ir-banner-text { flex: 1; white-space: pre-wrap; }
.ir-banner-x { background: none; border: none; cursor: pointer; color: inherit; padding: 2px; }
.ir-banner-error { background: #fdecea; border-color: #f2c4bd; color: #8c1d18; }
.ir-banner-warn { background: #fff8e1; border-color: #ecdba4; color: #6d4f00; }
.ir-banner-ok { background: #e9f5ea; border-color: #c0e0c3; color: #1e5b24; }

/* --- settings chips ---------------------------------------------------------- */

.ir-chips {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
    padding: 7px 10px;
    background: #fbfcfd;
    border-bottom: 1px solid var(--ir-border-light);
}
.ir-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    background: var(--ir-bg);
    border: 1px solid #c8d3dd;
    border-radius: var(--ir-radius);
    padding: 2px 4px 2px 6px;
    font-size: 12px;
}
.ir-chip-off .ir-chip-label { opacity: .5; }
.ir-chip-check { margin: 0; }
.ir-chip-swatch { width: 11px; height: 11px; border-radius: 2px; border: 1px solid rgba(0,0,0,.2); }
.ir-chip-label {
    background: none;
    border: none;
    font: inherit;
    font-size: 12px;
    color: var(--ir-text);
    cursor: pointer;
    padding: 1px 2px;
}
.ir-chip-label:hover { color: var(--ir-accent); }
.ir-chip-label b { font-weight: 600; }
.ir-chip-x { background: none; border: none; cursor: pointer; color: var(--ir-text-muted); padding: 2px 3px; line-height: 0; }
.ir-chip-x:hover { color: var(--ir-danger); }

/* --- grid ---------------------------------------------------------------- */

.ir-tablewrap { overflow-x: auto; }
.ir-table {
    width: 100%;
    border-collapse: collapse;
    font-size: var(--ir-font-size);
}
.ir-table th {
    background: var(--ir-bg-header);
    color: #3d4854;
    font-weight: 600;
    font-size: 12px;
    text-align: left;
    padding: 7px 12px;
    border-bottom: 1px solid var(--ir-border);
    white-space: nowrap;
    user-select: none;
}
.ir-table th.ir-th-menu { cursor: pointer; }
.ir-table th.ir-th-menu:hover { background: #e8edf2; color: var(--ir-accent); }
.ir-th-inner { display: inline-flex; align-items: center; gap: 4px; }
.ir-table th.ir-num, .ir-table td.ir-num { text-align: right; }
.ir-table th.ir-num .ir-th-inner { justify-content: flex-end; }
.ir-sort-dir { color: var(--ir-accent); font-size: 8px; }
.ir-sort-ord { color: var(--ir-text-muted); font-size: 9px; vertical-align: super; }

.ir-table td {
    padding: 5px 12px;
    border-bottom: 1px solid #eef1f3;
    white-space: nowrap;
}
.ir-table td.ir-num { font-variant-numeric: tabular-nums; }
.ir-table td.ir-date { font-variant-numeric: tabular-nums; }
.ir-table tr.ir-row:hover td { background-color: #f5f9fd; }

.ir-table tr.ir-break-header td {
    background: #e9eef4;
    border-top: 1px solid #c9d4de;
    font-weight: 600;
    color: #33404d;
}
.ir-break-count { float: right; font-weight: 400; color: var(--ir-text-muted); font-size: 12px; }

.ir-table tr.ir-break-total td { background: #f7f9fb; color: #3e4a56; font-style: italic; }
.ir-table tr.ir-grand-total td {
    background: #f0f5fa;
    border-top: 2px solid #a9bccc;
    font-weight: 600;
}
.ir-agg-fn { font-style: normal; font-weight: 600; font-size: 12px; color: var(--ir-text-muted); }
.ir-table tr.ir-empty td { text-align: center; color: var(--ir-text-muted); padding: 26px; }

/* --- chart view ---------------------------------------------------------------- */

.ir-chart-region {
    position: relative;
    height: 22rem;
    padding: 14px 14px 8px;
}
.ir-chart-empty { padding: 26px; text-align: center; color: var(--ir-text-muted); }
.ir-chart-data { border-top: 1px solid var(--ir-border-light); }
.ir-chart-data summary {
    cursor: pointer;
    padding: 7px 12px;
    font-size: 12px;
    color: var(--ir-text-muted);
}
.ir-chart-data summary:hover { color: var(--ir-accent); }
.ir-chart-data .ir-tablewrap { max-height: 16rem; overflow-y: auto; }
.ir-chart-valuerow select { min-width: 0; }

/* --- pagination ---------------------------------------------------------------- */

.ir-pager {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 7px 10px;
    color: var(--ir-text-muted);
    font-size: 12px;
}
.ir-pager-left { display: inline-flex; align-items: center; gap: 8px; }
.ir-page-btn { padding: 2px 9px; font-size: 14px; line-height: 1.2; }
.ir-page-info { min-width: 8rem; text-align: center; }
.ir-pagesize-wrap { margin-left: 10px; display: inline-flex; align-items: center; gap: 5px; }
.ir-pagesize { padding: 2px 4px; font-size: 12px; }

/* --- popup menus ---------------------------------------------------------------- */

.ir-popup {
    z-index: 1000;
    min-width: 190px;
    background: var(--ir-bg);
    border: 1px solid #ccd4dc;
    border-radius: 4px;
    box-shadow: 0 6px 18px rgba(31, 39, 51, .18);
    padding: 4px 0;
    font-size: var(--ir-font-size);
    color: var(--ir-text);
}
.ir-menu-item {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    text-align: left;
    background: none;
    border: none;
    font: inherit;
    font-size: var(--ir-font-size);
    color: var(--ir-text);
    padding: 6px 14px 6px 8px;
    cursor: pointer;
}
.ir-menu-item:hover:not(:disabled), .ir-menu-item:focus-visible { background: var(--ir-accent-soft); outline: none; }
.ir-menu-item:disabled { color: #a6adb5; cursor: default; }
.ir-menu-check { width: 13px; font-size: 11px; color: var(--ir-accent); flex: none; }
.ir-menu-label { flex: 1; }
.ir-menu-hint { color: var(--ir-text-muted); font-size: 11px; }
.ir-menu-heading {
    padding: 8px 12px 3px;
    font-size: 10.5px;
    font-weight: 600;
    letter-spacing: .05em;
    text-transform: uppercase;
    color: var(--ir-text-muted);
}
.ir-menu-sep { height: 1px; background: var(--ir-border-light); margin: 4px 0; }

/* --- dialogs ---------------------------------------------------------------- */

.ir-overlay {
    position: fixed;
    inset: 0;
    z-index: 1100;
    background: rgba(23, 32, 44, .45);
    display: flex;
    align-items: flex-start;
    justify-content: center;
    padding: 6vh 16px 16px;
}
.ir-dialog {
    background: var(--ir-bg);
    color: var(--ir-text);
    border-radius: 6px;
    box-shadow: 0 14px 40px rgba(15, 23, 33, .35);
    width: 30rem;
    max-width: 100%;
    max-height: 86vh;
    display: flex;
    flex-direction: column;
    font-size: var(--ir-font-size);
}
.ir-dialog-title {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 11px 16px;
    font-weight: 600;
    font-size: 14px;
    border-bottom: 1px solid var(--ir-border-light);
}
.ir-dialog-x { background: none; border: none; cursor: pointer; color: var(--ir-text-muted); padding: 4px; line-height: 0; }
.ir-dialog-x:hover { color: var(--ir-danger); }
.ir-dialog-body { padding: 14px 16px; overflow-y: auto; }
.ir-dialog-footer {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 11px 16px;
    border-top: 1px solid var(--ir-border-light);
}
.ir-dialog-error {
    margin: 10px 16px 0;
    padding: 7px 11px;
    border-radius: var(--ir-radius);
    background: #fdecea;
    border: 1px solid #f2c4bd;
    color: #8c1d18;
    font-size: 12.5px;
}
.ir-dialog-note { color: var(--ir-text-muted); font-size: 12px; margin: 10px 0 0; }
.ir-confirm-text { margin: 0; }

.ir-field { display: flex; flex-direction: column; gap: 4px; margin-bottom: 12px; }
.ir-field[hidden] { display: none; }
.ir-field-label { font-size: 11.5px; font-weight: 600; color: var(--ir-text-muted); }
.ir-condition-head, .ir-gap-above { margin-top: 12px; }
.ir-field .ir-input, .ir-field .ir-select { align-self: flex-start; min-width: 14rem; }
.ir-field .ir-input-wide { min-width: 16rem; }
.ir-checkline { display: flex; align-items: center; gap: 7px; margin: 4px 0 8px; }

.ir-value-editor { display: inline-flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.ir-value-and { color: var(--ir-text-muted); font-size: 12px; }
.ir-value-editor .ir-input { min-width: 9rem; }

.ir-dlgrow { display: flex; align-items: center; gap: 6px; margin-bottom: 7px; }
.ir-row-of { color: var(--ir-text-muted); font-size: 12px; }
.ir-row-x { padding: 2px 8px; color: var(--ir-text-muted); }
.ir-row-x:hover { color: var(--ir-danger); }
.ir-add-row { margin-top: 2px; font-size: 12px; }

.ir-shuttle { display: flex; align-items: center; gap: 10px; }
.ir-shuttle-col { display: flex; flex-direction: column; gap: 5px; flex: 1; min-width: 0; }
.ir-shuttle-head { font-size: 11.5px; font-weight: 600; color: var(--ir-text-muted); }
.ir-shuttle-list { width: 100%; min-height: 15rem; font: inherit; font-size: 12.5px; border: 1px solid #c3cbd3; border-radius: var(--ir-radius); }
.ir-shuttle-btns { display: flex; flex-direction: column; gap: 5px; }
.ir-shuttle-btns .ir-btn { padding: 3px 9px; justify-content: center; }

.ir-token-group { margin-bottom: 10px; }
.ir-token-group > div { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 5px; }
.ir-token {
    font: inherit;
    font-size: 11px;
    background: #eef3f8;
    border: 1px solid #cddcea;
    border-radius: 10px;
    padding: 2px 9px;
    cursor: pointer;
    color: #2c4a63;
}
.ir-token:hover { background: #dcebf7; }

.ir-colors { display: flex; gap: 18px; margin-top: 6px; }
.ir-color-pick { display: flex; align-items: center; gap: 7px; font-size: 12.5px; }
.ir-color { width: 36px; height: 24px; padding: 1px; border: 1px solid #c3cbd3; border-radius: var(--ir-radius); background: var(--ir-bg); }

/* --- admin widget ---------------------------------------------------------------- */

.ir-admin-bar {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
    padding: 8px 10px;
    background: var(--ir-bg-soft);
    border-bottom: 1px solid var(--ir-border);
    border-radius: 4px 4px 0 0;
}
.ir-admin-count { color: var(--ir-text-muted); font-size: 12px; }
.ir-badge {
    display: inline-block;
    font-size: 11px;
    padding: 1px 8px;
    border-radius: 9px;
    border: 1px solid;
}
.ir-badge-global { background: #e3f0fb; border-color: #b7d6f2; color: #0b5394; }
.ir-badge-private { background: #f1f2f3; border-color: #d9dcdf; color: #555e66; }
.ir-linkbtn {
    background: none;
    border: none;
    font: inherit;
    font-size: 12px;
    color: var(--ir-accent);
    cursor: pointer;
    padding: 1px 3px;
}
.ir-linkbtn:hover { text-decoration: underline; }
.ir-linkbtn-danger { color: var(--ir-danger); }
.ir-actions-cell { white-space: nowrap; }
.ir-state-pre {
    background: var(--ir-bg-soft);
    border: 1px solid var(--ir-border-light);
    border-radius: 4px;
    padding: 10px;
    max-height: 55vh;
    overflow: auto;
    font-size: 12px;
    font-family: ui-monospace, Consolas, monospace;
    white-space: pre;
    margin: 0;
}
`;function r(n,e={},...t){let i=document.createElement(n);for(let[o,s]of Object.entries(e))s!=null&&(o==="class"?i.className=s:o==="part"?i.setAttribute("part",s):o==="for"?i.htmlFor=s:o==="dataset"?Object.assign(i.dataset,s):o==="style"?Object.assign(i.style,s):o.startsWith("on")||o in i?i[o]=s:i.setAttribute(o,s));return i.append(...t.flat(1/0).filter(o=>o!=null&&o!==!1)),i}function O(n){let e=n.attachShadow({mode:"open"}),t=r("style",{"data-ir-styles":""});t.textContent=A;let i=r("div",{part:"surface"});return e.append(t,i),{root:e,mount:i}}var H={search:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><circle cx="6.5" cy="6.5" r="4.5" fill="none" stroke="currentColor" stroke-width="1.6"/><line x1="10" y1="10" x2="14" y2="14" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',caret:'<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 5.5 8 11l5-5.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',grid:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M1.5 9.5h13 M6 2.5v11 M11 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>',group:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><rect x="1.5" y="2" width="6" height="3" fill="currentColor" opacity=".55"/><rect x="1.5" y="6.5" width="10" height="3" fill="currentColor" opacity=".8"/><rect x="1.5" y="11" width="13" height="3" fill="currentColor"/></svg>',pivot:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M6 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/><circle cx="10.5" cy="10" r="1.4" fill="currentColor"/></svg>',chart:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><rect x="2" y="8" width="3" height="6" fill="currentColor" opacity=".65"/><rect x="6.5" y="3.5" width="3" height="10.5" fill="currentColor"/><rect x="11" y="6" width="3" height="8" fill="currentColor" opacity=".8"/></svg>',close:'<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 3l10 10M13 3L3 13" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>'};function j(n){let e=r("span",{class:"ir-icon","aria-hidden":"true"});return e.innerHTML=H[n]??"",e}function v(n,e,t){return r("div",{class:`ir-banner ir-banner-${n}`},r("span",{class:"ir-banner-text"},e),t?r("button",{type:"button",class:"ir-banner-x","aria-label":"Dismiss",onclick:t},j("close")):null)}var D=null,B=null,m=new WeakMap;function M(){D?.(),D=null,B=null}function U(n){B===n&&M();for(let e of[...m.get(n)??[]])e.close();m.delete(n)}function w({owner:n,title:e,width:t,cls:i,build:o,applyLabel:s="Apply",onApply:c,destructive:y=!1}){M();let h=n?.shadowRoot??document,P=h instanceof ShadowRoot?h:document.body,_=h.activeElement??document.activeElement,f=r("div",{class:"ir-dialog-error",hidden:!0}),b=r("div",{class:"ir-dialog-body"}),g=null;n&&(g=m.get(n)??new Set,m.set(n,g));let C=!1,l={root:null,body:b,close(){C||(C=!0,l.root.remove(),document.removeEventListener("keydown",S,!0),g?.delete(l),_?.focus?.())},setError(a){if(f.replaceChildren(),a==null){f.hidden=!0;return}let d=[];if(a.errors&&typeof a.errors=="object"){a.problem?.title&&d.push(a.problem.title);for(let p of Object.values(a.errors))d.push(...p)}else d.push(typeof a=="string"?a:a.message||"Something went wrong.");f.append(...d.map(p=>r("div",{},p))),f.hidden=!1}},x=c?r("button",{type:"button",class:"ir-btn ir-btn-primary"+(y?" ir-btn-danger":""),onclick:async()=>{l.setError(null);let a=l.root.querySelectorAll(".ir-dialog-footer button");a.forEach(d=>d.disabled=!0);try{await c(l),l.close()}catch(d){l.setError(d)}finally{a.forEach(d=>d.disabled=!1)}}},s):null,E=r("button",{type:"button",class:"ir-btn",onclick:()=>l.close()},c?"Cancel":"Close");l.root=r("div",{class:"ir-overlay"+(i?` ${i}`:""),part:"dialog-overlay"},r("div",{class:"ir-dialog",part:"dialog",role:"dialog","aria-modal":"true",style:t?{width:t}:{}},r("div",{class:"ir-dialog-title"},e,r("button",{type:"button",class:"ir-dialog-x","aria-label":"Close",onclick:()=>l.close()},j("close"))),f,b,r("div",{class:"ir-dialog-footer"},E,x)));let S=a=>{if(a.key==="Escape"){a.stopPropagation(),l.close();return}let d=a.composedPath?.()[0]??a.target;if(a.key==="Enter"&&x&&d.tagName!=="TEXTAREA"&&d.tagName!=="BUTTON"){a.preventDefault(),x.click();return}if(a.key!=="Tab")return;let p=[...l.root.querySelectorAll("button, input, select, textarea, [tabindex]")].filter(R=>!R.disabled&&R.offsetParent!==null);if(!p.length)return;let $=p[0],T=p[p.length-1],L=h.activeElement??document.activeElement;a.shiftKey&&L===$?(a.preventDefault(),T.focus()):!a.shiftKey&&L===T&&(a.preventDefault(),$.focus())};return document.addEventListener("keydown",S,!0),o(b,l),P.append(l.root),g?.add(l),(b.querySelector("input, select, textarea")??x??E).focus(),l}function q(n,e,t,i="Delete"){return new Promise(o=>{let s=!1,c=w({owner:n,title:e,width:"26rem",applyLabel:i,destructive:!0,build:h=>h.append(r("p",{class:"ir-confirm-text"},t)),onApply:()=>{s=!0}}),y=c.close;c.close=()=>{y(),o(s)}})}function N(n,e,t={}){return r("label",{class:"ir-field"+(t.inline?" ir-field-inline":"")},r("span",{class:"ir-field-label"},n),e)}var W=new URL("..",import.meta.url).pathname.replace(/\/$/,""),z=class extends HTMLElement{static observedAttributes=["api-base","base"];constructor(){super();let{root:e,mount:t}=O(this);this._root=e,this._mount=t,this._seq=0}get apiBase(){return this.getAttribute("api-base")??this.getAttribute("base")??W}set apiBase(e){e==null?this.removeAttribute("api-base"):this.setAttribute("api-base",String(e))}get base(){return this.apiBase.replace(/\/+$/,"")}connectedCallback(){this._connected=!0,this.init()}disconnectedCallback(){this._connected=!1,++this._seq,U(this)}attributeChangedCallback(e,t,i){this._connected&&t!==i&&this.init()}async init(){let e=++this._seq;this.rows=[],this.whoami=null;let t=r("input",{class:"ir-input",type:"search",placeholder:"Filter by report, title, owner\u2026",oninput:()=>this.renderTable()});this.els={filter:t,count:r("span",{class:"ir-admin-count"}),identity:r("span",{class:"ir-admin-count"}),errorSlot:r("div",{}),transientSlot:r("div",{}),body:r("div",{class:"ir-tablewrap",part:"table-container"})},this._mount.replaceChildren(r("div",{class:"ir-admin-bar",part:"toolbar"},t,r("button",{type:"button",class:"ir-btn",onclick:()=>this.reload()},"Refresh"),this.els.count,r("span",{class:"ir-spacer"}),this.els.identity),r("div",{class:"ir-notices",part:"notices"},this.els.errorSlot,this.els.transientSlot),this.els.body),this.whoami=await u(`${this.base}/whoami`).catch(()=>null),!(e!==this._seq||!this.isConnected)&&(this.whoami?.identity&&(this.els.identity.textContent=`Signed in as ${this.whoami.identity}`),await this.reload())}async reload(){let e=this._seq;this.els.errorSlot.replaceChildren();try{let t=await u(`${this.base}/admin/saved`);if(e!==this._seq||!this.isConnected)return;this.rows=t,this.renderTable()}catch(t){if(e!==this._seq||!this.isConnected)return;let i=t.status===401?"Sign in to administer saved reports.":t.status===404?"Administrator access required. Add your identity to InteractiveReport:Administrators.":t.message;this.els.body.replaceChildren(),this.els.errorSlot.replaceChildren(v("error",i))}}notify(e){let t=v("ok",e);this.els.transientSlot.append(t),setTimeout(()=>t.remove(),4e3)}fail(e){this.els.errorSlot.replaceChildren(v("error",e.message,()=>this.els.errorSlot.replaceChildren()))}filtered(){let e=this.els.filter.value.trim().toLowerCase();return e?this.rows.filter(t=>[t.reportName,t.title,t.owner].some(i=>(i??"").toLowerCase().includes(e))):this.rows}renderTable(){let e=this.filtered();this.els.count.textContent=e.length===this.rows.length?`${this.rows.length} saved`:`${e.length} of ${this.rows.length} saved`;let t=(o,s,c)=>r("button",{type:"button",class:"ir-linkbtn"+(c?" ir-linkbtn-danger":""),onclick:s},o),i=e.map(o=>r("tr",{class:"ir-row"},r("td",{},o.reportName),r("td",{},o.title),r("td",{},o.owner),r("td",{},r("span",{class:"ir-badge "+(o.isGlobal?"ir-badge-global":"ir-badge-private")},o.isGlobal?"Global":"Private")),r("td",{class:"ir-date"},F(o.modifiedUtc)),r("td",{class:"ir-actions-cell"},t(o.isGlobal?"Unpublish":"Publish",()=>this.setGlobal(o,!o.isGlobal))," \xB7 ",t("Reassign\u2026",()=>this.reassign(o))," \xB7 ",t("State",()=>this.viewState(o))," \xB7 ",t("Delete\u2026",()=>this.remove(o),!0))));i.length||i.push(r("tr",{class:"ir-empty"},r("td",{colSpan:6},"No saved reports."))),this.els.body.replaceChildren(r("table",{class:"ir-table",part:"table"},r("thead",{},r("tr",{},...["Report","Title","Owner","Scope","Modified",""].map(o=>r("th",{scope:"col"},o)))),r("tbody",{},...i)))}async setGlobal(e,t){try{await u(`${this.base}/saved/${encodeURIComponent(e.id)}`,{method:"PUT",body:{isGlobal:t}}),this.notify(t?`"${e.title}" is now global.`:`"${e.title}" is now private to ${e.owner}.`),await this.reload()}catch(i){this.fail(i)}}reassign(e){let t=r("input",{class:"ir-input",type:"text",value:e.owner});w({owner:this,title:"Reassign Owner",width:"26rem",applyLabel:"Reassign",build:i=>i.append(r("p",{class:"ir-confirm-text"},`"${e.title}" (${e.reportName})`),N("New owner (identity value)",t),r("p",{class:"ir-dialog-note"},"The exact identity value \u2014 what GET \u2026/whoami reports for that user.")),onApply:async()=>{let i=t.value.trim();if(!i)throw new Error("Enter an identity value");await u(`${this.base}/saved/${encodeURIComponent(e.id)}`,{method:"PUT",body:{owner:i}}),this.notify(`"${e.title}" reassigned to ${i}.`),await this.reload()}})}async viewState(e){try{let t=await u(`${this.base}/saved/${encodeURIComponent(e.id)}`);w({owner:this,title:`${e.title} \u2014 state document`,width:"36rem",build:i=>i.append(r("pre",{class:"ir-state-pre"},JSON.stringify(t.state,null,2)))})}catch(t){this.fail(t)}}async remove(e){let t=e.isGlobal?"the GLOBAL report":`${e.owner}'s report`;if(await q(this,"Delete Saved Report",`Delete ${t} "${e.title}"? This cannot be undone.`))try{await u(`${this.base}/saved/${encodeURIComponent(e.id)}`,{method:"DELETE"}),this.notify(`"${e.title}" deleted.`),await this.reload()}catch(i){this.fail(i)}}};function F(n){let e=new Date(n);return Number.isNaN(e.valueOf())?n??"":e.toLocaleString()}customElements.get("interactive-report-admin")||customElements.define("interactive-report-admin",z);
//# sourceMappingURL=ir-admin.js.map
