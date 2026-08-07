var H=class extends Error{constructor(e,t){let r=[];e.title&&r.push(e.title),e.detail&&r.push(e.detail);for(let a of Object.values(e.errors??{}))for(let n of a)r.push(n);super(r.join(" \u2014 ")||`HTTP ${t}`),this.name="ApiError",this.status=t,this.problem=e,this.errors=e.errors??null,this.traceId=e.traceId??null}};async function ne(i){let e=await i.json().catch(()=>({}));return new H(e,i.status)}async function O(i,{method:e="GET",body:t,signal:r}={}){let a=await fetch(i,{method:e,signal:r,headers:t!==void 0?{"Content-Type":"application/json"}:void 0,body:t!==void 0?JSON.stringify(t):void 0});if(!a.ok)throw await ne(a);return a.status===204?null:a.json()}async function se(i,e){let t=await fetch(i,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(e)});if(!t.ok)throw await ne(t);let r=t.headers.get("Content-Disposition")??"",a=/filename="?([^";]+)"?/.exec(r)?.[1]??null;return{blob:await t.blob(),filename:a,truncated:t.headers.get("X-IR-Truncated")==="true"}}function le(i,e){let t=document.createElement("a");t.href=URL.createObjectURL(i),t.download=e,t.click(),URL.revokeObjectURL(t.href)}var ce=`/* InteractiveReport widget theme \u2014 an APEX Universal-Theme-flavored skin.
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
`;function o(i,e={},...t){let r=document.createElement(i);for(let[a,n]of Object.entries(e))n!=null&&(a==="class"?r.className=n:a==="part"?r.setAttribute("part",n):a==="for"?r.htmlFor=n:a==="dataset"?Object.assign(r.dataset,n):a==="style"?Object.assign(r.style,n):a.startsWith("on")||a in r?r[a]=n:r.setAttribute(a,n));return r.append(...t.flat(1/0).filter(a=>a!=null&&a!==!1)),r}function de(i){let e=i.attachShadow({mode:"open"}),t=o("style",{"data-ir-styles":""});t.textContent=ce;let r=o("div",{part:"surface"});return e.append(t,r),{root:e,mount:r}}var Oe={search:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><circle cx="6.5" cy="6.5" r="4.5" fill="none" stroke="currentColor" stroke-width="1.6"/><line x1="10" y1="10" x2="14" y2="14" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',caret:'<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 5.5 8 11l5-5.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',grid:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M1.5 9.5h13 M6 2.5v11 M11 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>',group:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><rect x="1.5" y="2" width="6" height="3" fill="currentColor" opacity=".55"/><rect x="1.5" y="6.5" width="10" height="3" fill="currentColor" opacity=".8"/><rect x="1.5" y="11" width="13" height="3" fill="currentColor"/></svg>',pivot:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M6 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/><circle cx="10.5" cy="10" r="1.4" fill="currentColor"/></svg>',chart:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><rect x="2" y="8" width="3" height="6" fill="currentColor" opacity=".65"/><rect x="6.5" y="3.5" width="3" height="10.5" fill="currentColor"/><rect x="11" y="6" width="3" height="8" fill="currentColor" opacity=".8"/></svg>',close:'<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 3l10 10M13 3L3 13" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>'};function B(i){let e=o("span",{class:"ir-icon","aria-hidden":"true"});return e.innerHTML=Oe[i]??"",e}function F(i,e,t){return o("div",{class:`ir-banner ir-banner-${i}`},o("span",{class:"ir-banner-text"},e),t?o("button",{type:"button",class:"ir-banner-x","aria-label":"Dismiss",onclick:t},B("close")):null)}var q=null,V=null,j=new WeakMap;function N(){q?.(),q=null,V=null}function pe(i){V===i&&N();for(let e of[...j.get(i)??[]])e.close();j.delete(i)}function _(i,e){N();let t=o("div",{class:"ir-popup",part:"menu",role:"menu"}),r=i.getRootNode(),a=r instanceof ShadowRoot?r:document.body;V=r instanceof ShadowRoot?r.host:null;for(let p of e){if(p==="-"){t.append(o("div",{class:"ir-menu-sep",role:"separator"}));continue}if(p.heading!==void 0){t.append(o("div",{class:"ir-menu-heading"},p.heading));continue}let g=o("button",{type:"button",class:"ir-menu-item"+(p.checked?" ir-checked":""),role:"menuitem",disabled:p.disabled===!0,onclick:()=>{N(),p.onPick?.()}},o("span",{class:"ir-menu-check","aria-hidden":"true"},p.checked?"\u2713":""),o("span",{class:"ir-menu-label"},p.label),p.hint?o("span",{class:"ir-menu-hint"},p.hint):null);t.append(g)}a.append(t);let n=i.getBoundingClientRect();t.style.position="fixed",t.style.visibility="hidden",t.style.left="0",t.style.top="0";let s=t.getBoundingClientRect(),l=Math.min(n.left,window.innerWidth-s.width-8),c=n.bottom+2;c+s.height>window.innerHeight-8&&(c=Math.max(8,n.top-s.height-2)),t.style.left=`${Math.max(8,l)}px`,t.style.top=`${c}px`,t.style.visibility="";let b=p=>{let g=p.composedPath?.()??[p.target];!g.includes(t)&&!g.includes(i)&&N()},m=p=>{if(p.key==="Escape"){N(),i.focus?.();return}if(p.key!=="ArrowDown"&&p.key!=="ArrowUp"&&p.key!=="Home"&&p.key!=="End")return;let g=[...t.querySelectorAll(".ir-menu-item:not([disabled])")];if(!g.length)return;p.preventDefault();let v=g.indexOf(r.activeElement??document.activeElement),S=p.key==="Home"?0:p.key==="End"?g.length-1:p.key==="ArrowDown"?(v+1)%g.length:(v-1+g.length)%g.length;g[S].focus()},d=!1;requestAnimationFrame(()=>{d=!0});let h=()=>{d&&N()};return document.addEventListener("mousedown",b,!0),document.addEventListener("keydown",m,!0),window.addEventListener("scroll",h,!0),window.addEventListener("resize",h),q=()=>{document.removeEventListener("mousedown",b,!0),document.removeEventListener("keydown",m,!0),window.removeEventListener("scroll",h,!0),window.removeEventListener("resize",h),t.remove()},t.querySelector(".ir-menu-item:not([disabled])")?.focus(),t}function A({owner:i,title:e,width:t,cls:r,build:a,applyLabel:n="Apply",onApply:s,destructive:l=!1}){N();let c=i?.shadowRoot??document,b=c instanceof ShadowRoot?c:document.body,m=c.activeElement??document.activeElement,d=o("div",{class:"ir-dialog-error",hidden:!0}),h=o("div",{class:"ir-dialog-body"}),p=null;i&&(p=j.get(i)??new Set,j.set(i,p));let g=!1,v={root:null,body:h,close(){g||(g=!0,v.root.remove(),document.removeEventListener("keydown",u,!0),p?.delete(v),m?.focus?.())},setError(f){if(d.replaceChildren(),f==null){d.hidden=!0;return}let y=[];if(f.errors&&typeof f.errors=="object"){f.problem?.title&&y.push(f.problem.title);for(let C of Object.values(f.errors))y.push(...C)}else y.push(typeof f=="string"?f:f.message||"Something went wrong.");d.append(...y.map(C=>o("div",{},C))),d.hidden=!1}},S=s?o("button",{type:"button",class:"ir-btn ir-btn-primary"+(l?" ir-btn-danger":""),onclick:async()=>{v.setError(null);let f=v.root.querySelectorAll(".ir-dialog-footer button");f.forEach(y=>y.disabled=!0);try{await s(v),v.close()}catch(y){v.setError(y)}finally{f.forEach(y=>y.disabled=!1)}}},n):null,x=o("button",{type:"button",class:"ir-btn",onclick:()=>v.close()},s?"Cancel":"Close");v.root=o("div",{class:"ir-overlay"+(r?` ${r}`:""),part:"dialog-overlay"},o("div",{class:"ir-dialog",part:"dialog",role:"dialog","aria-modal":"true",style:t?{width:t}:{}},o("div",{class:"ir-dialog-title"},e,o("button",{type:"button",class:"ir-dialog-x","aria-label":"Close",onclick:()=>v.close()},B("close"))),d,h,o("div",{class:"ir-dialog-footer"},x,S)));let u=f=>{if(f.key==="Escape"){f.stopPropagation(),v.close();return}let y=f.composedPath?.()[0]??f.target;if(f.key==="Enter"&&S&&y.tagName!=="TEXTAREA"&&y.tagName!=="BUTTON"){f.preventDefault(),S.click();return}if(f.key!=="Tab")return;let C=[...v.root.querySelectorAll("button, input, select, textarea, [tabindex]")].filter(L=>!L.disabled&&L.offsetParent!==null);if(!C.length)return;let E=C[0],w=C[C.length-1],k=c.activeElement??document.activeElement;f.shiftKey&&k===E?(f.preventDefault(),w.focus()):!f.shiftKey&&k===w&&(f.preventDefault(),E.focus())};return document.addEventListener("keydown",u,!0),a(h,v),b.append(v.root),p?.add(v),(h.querySelector("input, select, textarea")??S??x).focus(),v}function G(i,e,t,r="Delete"){return new Promise(a=>{let n=!1,s=A({owner:i,title:e,width:"26rem",applyLabel:r,destructive:!0,build:c=>c.append(o("p",{class:"ir-confirm-text"},t)),onApply:()=>{n=!0}}),l=s.close;s.close=()=>{l(),a(n)}})}function z(i,e,t={}){return o("label",{class:"ir-field"+(t.inline?" ir-field-inline":"")},o("span",{class:"ir-field-label"},i),e)}function R(i,e){let t=o("select",{class:"ir-select"}),r=a=>typeof a=="string"?new Option(a,a):new Option(a.label,a.value);for(let a of i)if(a.options){let n=o("optgroup",{label:a.label});n.append(...a.options.map(r)),t.append(n)}else t.append(r(a));return e!=null&&(t.value=e),t}function Y(i,e,t=!1){if(i==null)return"";if(typeof i=="boolean")return i?"true":"false";if(e==="number"&&typeof i=="number")return!t&&Number.isInteger(i)?String(i):i.toLocaleString(void 0,{minimumFractionDigits:2,maximumFractionDigits:2});if(e==="date"){let r=String(i);return r.endsWith("T00:00:00")?r.slice(0,10):r.replace("T"," ")}return String(i)}function ue(i){return i==null?"\u2014":typeof i=="number"?i.toLocaleString(void 0,{maximumFractionDigits:2}):String(i)}var D={sum:"Sum",avg:"Avg",min:"Min",max:"Max",count:"Count",countDistinct:"Count Distinct"},ze=["sum","avg","min","max","count","countDistinct"];function $({w:i,kind:e,index:t,text:r,colLabel:a,off:n,toggleable:s=!0,removable:l=!0,swatch:c}){let b=o("span",{class:"ir-chip"+(n?" ir-chip-off":""),dataset:{kind:e}});s&&b.append(o("input",{type:"checkbox",class:"ir-chip-check",checked:!n,title:n?"Enable":"Disable",onchange:d=>i.chipToggle(e,t,d.target.checked)})),c&&b.append(o("span",{class:"ir-chip-swatch",style:{background:c}}));let m=o("button",{type:"button",class:"ir-chip-label",title:"Edit",onclick:()=>i.chipEdit(e,t)});return a&&m.append(o("b",{},a)," "),m.append(r),b.append(m),l&&b.append(o("button",{type:"button",class:"ir-chip-x","aria-label":"Remove",title:"Remove",onclick:()=>i.chipRemove(e,t)},B("close"))),b}function U(i,e){let t=i.doc,r=[];t.search&&r.push($({w:i,kind:"search",index:0,toggleable:!1,colLabel:"Search",text:`'${t.search}'`})),(t.filters??[]).forEach((a,n)=>r.push($({w:i,kind:"filter",index:n,off:a.enabled===!1,colLabel:"Filter",text:a.expr}))),(t.breaks??[]).forEach((a,n)=>r.push($({w:i,kind:"break",index:n,toggleable:!1,colLabel:"Break",text:i.labelOf(a)}))),(t.aggregates??[]).forEach((a,n)=>r.push($({w:i,kind:"aggregate",index:n,toggleable:!1,colLabel:"\u03A3",text:`${D[a.fn]??a.fn} of ${i.labelOf(a.col)}`}))),(t.computed??[]).forEach((a,n)=>r.push($({w:i,kind:"computed",index:n,off:a.enabled===!1,colLabel:"\u0192",text:a.label??a.id}))),(t.highlights??[]).forEach((a,n)=>r.push($({w:i,kind:"highlight",index:n,off:a.enabled===!1,swatch:a.style?.bg??"#fff3a0",colLabel:"Highlight",text:a.expr+(a.scope==="cell"?` (${i.labelOf(a.col)} cell)`:" (row)")}))),t.view?.mode==="groupBy"?r.push($({w:i,kind:"view",index:0,toggleable:!1,colLabel:"Group by",text:(t.view.groupBy??[]).map(a=>i.labelOf(a)).join(", ")})):t.view?.mode==="pivot"?r.push($({w:i,kind:"view",index:0,toggleable:!1,colLabel:"Pivot",text:`${(t.view.rows??[]).map(a=>i.labelOf(a)).join(", ")} \xD7 ${(t.view.cols??[]).map(a=>i.labelOf(a)).join(", ")}`})):t.view?.mode==="chart"&&r.push($({w:i,kind:"view",index:0,toggleable:!1,colLabel:"Chart",text:fe(i,t.view)})),e.replaceChildren(...r),e.hidden=r.length===0}function he(i,e){let t=i.lastResult;if(!t){e.replaceChildren();return}let r=i.doc.view?.mode??"grid",a=t.columns,n=new Map((i.doc.sorts??[]).map((u,f)=>[u.col,{dir:u.dir??"asc",ord:f+1}])),s=r==="groupBy"?new Set(i.doc.view?.groupBy??[]):null,l=o("tr",{});for(let u of a){let f=r==="grid"||r==="groupBy"&&s.has(u.name),y=n.get(u.name),C=o("span",{class:"ir-th-inner"},u.label);y&&(C.append(o("span",{class:"ir-sort-dir","aria-hidden":"true"},y.dir==="desc"?"\u25BC":"\u25B2")),(i.doc.sorts??[]).length>1&&C.append(o("span",{class:"ir-sort-ord"},String(y.ord))));let E=o("th",{class:(u.type==="number"?"ir-num ":"")+(f?"ir-th-menu":""),scope:"col","aria-sort":y?y.dir==="desc"?"descending":"ascending":void 0},C);f&&(E.onclick=()=>i.openHeaderMenu(u.name,E)),l.append(E)}let c=r==="grid"?i.doc.breaks??[]:[],b=u=>c.map(f=>String(u[f]??"")).join(""),m=new Map((t.breakTotals??[]).map(u=>[b(u.key),u])),d=new Set(a.filter(u=>u.type==="number"&&t.rows.some(f=>typeof f[u.name]=="number"&&!Number.isInteger(f[u.name]))).map(u=>u.name)),h=new Map((i.doc.highlights??[]).map(u=>[u.id,u.style??{}])),p=new Map;for(let u of t.highlights??[])p.has(u.row)||p.set(u.row,[]),p.get(u.row).push(u);let g=(u,f)=>{let y=[],C=ze.filter(E=>Object.values(u??{}).some(w=>E in w));for(let E of C){let w=o("tr",{class:f});a.forEach((k,L)=>{let T=u[k.name]&&E in u[k.name],I=o("td",{class:k.type==="number"?"ir-num":""});L===0?(I.append(o("span",{class:"ir-agg-fn"},`${D[E]??E}:`)),T&&I.append(" ",ue(u[k.name][E]))):T&&(I.textContent=ue(u[k.name][E])),w.append(I)}),y.push(w)}return y},v=[],S=null,x=()=>{if(S===null)return;let u=m.get(S);u&&Object.keys(u.aggregates??{}).length&&v.push(...g(u.aggregates,"ir-break-total"))};for(let[u,f]of t.rows.entries()){if(c.length){let w=b(f);if(w!==S){x();let k=m.get(w),L=c.map(T=>`${i.labelOf(T)}: ${f[T]??"(blank)"}`).join("  \xB7  ");v.push(o("tr",{class:"ir-break-header"},o("td",{colSpan:a.length},o("span",{},L),k?o("span",{class:"ir-break-count"},`${Number(k.rows).toLocaleString()} rows`):null))),S=w}}let y=o("tr",{class:"ir-row"});for(let w of a){let k=[w.type==="number"?"ir-num":"",w.type==="date"?"ir-date":""].join(" ").trim();y.append(o("td",{class:k||void 0},Y(f[w.name],w.type,d.has(w.name))))}let C=(p.get(u)??[]).filter(w=>!w.col),E=(p.get(u)??[]).filter(w=>!!w.col);for(let w of[...C,...E]){let k=h.get(w.id)??{};if(!w.col)k.bg&&(y.style.background=k.bg),k.fg&&(y.style.color=k.fg);else{let L=a.findIndex(T=>T.name===w.col);L>=0&&(k.bg&&(y.children[L].style.background=k.bg),k.fg&&(y.children[L].style.color=k.fg))}}v.push(y)}x(),Object.keys(t.aggregates??{}).length&&v.push(...g(t.aggregates,"ir-grand-total")),t.rows.length||v.push(o("tr",{class:"ir-empty"},o("td",{colSpan:Math.max(a.length,1)},"No data found."))),e.replaceChildren(o("thead",{},l),o("tbody",{},...v))}var Le={bar:"Bar",line:"Line",area:"Line with Area",pie:"Pie"};function fe(i,e){let t=` by ${i.labelOf(e.label)}`;if(!e.fn)return i.labelOf(e.value)+t;let r=D[e.fn]??e.fn;return e.value?`${r} of ${i.labelOf(e.value)}${t}`:r+t}function be(i,e,t){let r=i.lastResult,a=i.doc.view;if(!r?.rows.length)return e.replaceChildren(o("div",{class:"ir-chart-empty"},"No data found.")),null;let[n,s]=r.columns,l=r.rows.map(p=>{let g=p[n.name];return g==null?"(blank)":Y(g,n.type)}),c=r.rows.map(p=>{let g=p[s.name];return g==null?null:Number(g)}),b=r.rows.some(p=>typeof p[s.name]=="number"&&!Number.isInteger(p[s.name])),m=`${Le[a.type]??"Chart"} chart of ${fe(i,a)}. ${l.length} data points.`,d=o("canvas",{class:"ir-chart-canvas",role:"img","aria-label":m}),h=o("table",{class:"ir-table ir-chart-table"},o("thead",{},o("tr",{},o("th",{scope:"col"},n.label),o("th",{scope:"col",class:"ir-num"},s.label))),o("tbody",{},...r.rows.map((p,g)=>o("tr",{},o("td",{},l[g]),o("td",{class:"ir-num"},Y(p[s.name],s.type,b))))));return e.replaceChildren(o("div",{class:"ir-chart-region"},d),o("details",{class:"ir-chart-data"},o("summary",{},"View chart data"),o("div",{class:"ir-tablewrap"},h))),d.getContext?.("2d")?t.renderChart(d,{type:a.type,horizontal:a.orientation==="horizontal",labels:l,values:c,metricLabel:s.label,labelAxisTitle:a.labelAxisTitle??null,valueAxisTitle:a.valueAxisTitle??null}):null}function ge(i,e){let t=i.lastResult;if(!t){e.replaceChildren();return}let{index:r,size:a}=t.page,n=t.totalRows,s=i.doc.view?.mode??"grid",l=s==="groupBy"?"groups":s==="chart"?"points":"rows",c=n===0?0:(r-1)*a+1,b=n===0?0:c+t.rows.length-1,m=Math.max(1,Math.ceil(n/a)),d=[...new Set([15,25,50,100,a])].filter(p=>p<=(i.schema?.limits?.maxPageSize??1/0)).sort((p,g)=>p-g),h=o("select",{class:"ir-select ir-pagesize",title:"Rows per page"},...d.map(p=>new Option(String(p),String(p))));h.value=String(a),h.onchange=()=>i.setPageSize(Number(h.value)),e.replaceChildren(o("div",{class:"ir-pager-left"},o("button",{type:"button",class:"ir-btn ir-page-btn",disabled:r<=1,"aria-label":"Previous page",onclick:()=>i.gotoPage(r-1)},"\u2039"),o("span",{class:"ir-page-info"},n===0?`0 ${l}`:`${c.toLocaleString()} \u2013 ${b.toLocaleString()} of ${Number(n).toLocaleString()} ${l}`),o("button",{type:"button",class:"ir-btn ir-page-btn",disabled:r>=m,"aria-label":"Next page",onclick:()=>i.gotoPage(r+1)},"\u203A"),s==="chart"?null:o("span",{class:"ir-pagesize-wrap"},"Rows ",h)),o("div",{class:"ir-pager-right"},`${t.elapsedMs} ms`))}function ve(i,e=50,t=null){let r=t?structuredClone(t):{};for(let[a,n]of Object.entries(i?structuredClone(i):{}))n!=null&&(r[a]=n);return r.filters??=[],r.sorts??=[],r.page={index:1,size:r.page?.size??e},r}function xe(i,e){let t=r=>{if(Array.isArray(r))return r.map(t);if(r&&typeof r=="object"){let a={};for(let[n,s]of Object.entries(r))n.startsWith("_")||s===void 0||(a[n]=t(s));return a}return r};return{...t(i),v:e}}function ye(i,e,t){let r=t.trim();if(!r)throw new Error("Enter a search value");switch(e){case"text":return`CONTAINS(${i}, ${me(r)})`;case"number":if(!/^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$/.test(r))throw new Error(`'${r}' is not a number`);return`${i} = ${r}`;case"date":if(!/^\d{4}-\d{2}-\d{2}$/.test(r))throw new Error(`'${r}' is not an ISO date (YYYY-MM-DD)`);return`${i} = TO_DATE(${me(r)})`;case"bool":{let a=r.toLowerCase();if(a==="true"||a==="1")return i;if(a==="false"||a==="0")return`NOT ${i}`;throw new Error(`'${r}' is not true or false`)}default:throw new Error(`Column '${i}' does not support scoped search`)}}function me(i){return`'${i.replaceAll("'","''")}'`}var we=[{value:"asc",label:"Ascending"},{value:"desc",label:"Descending"}];function P(i,{none:e}={}){let t=i.pickable().map(r=>({value:r.name,label:r.computed?`\u0192 ${r.label}`:r.label}));return e?[{value:"",label:e},...t]:t}function M(i,e,t,{addLabel:r="Add",max:a}={}){let n=l=>{if(a&&i.querySelectorAll(".ir-dlgrow").length>=a)return;let c=o("div",{class:"ir-dlgrow"});t(c,l),c.append(o("button",{type:"button",class:"ir-btn ir-row-x",title:"Remove","aria-label":"Remove row",onclick:()=>c.remove()},"\xD7")),i.append(c)};return e.forEach(n),e.length===0&&n(null),{addButton:o("button",{type:"button",class:"ir-btn ir-add-row",onclick:()=>n(null)},`+ ${r}`),read:()=>[...i.querySelectorAll(".ir-dlgrow")].map(l=>l._read()).filter(l=>l!=null)}}function K(i,{initial:e,placeholder:t,result:r,columns:a}){let n=o("textarea",{class:"ir-textarea",rows:r==="predicate"?4:3,spellcheck:!1,placeholder:t});n.value=e??"";let s=a??i.pickable(),l=d=>{let h=n.selectionStart??n.value.length;n.setRangeText(d,h,n.selectionEnd??h,"end"),n.focus()},c=(d,h)=>o("button",{type:"button",class:"ir-token",onclick:()=>l(h)},d),b=[c("="," = "),c("\u2260"," <> "),c("<"," < "),c("\u2264"," <= "),c(">"," > "),c("\u2265"," >= "),c("AND"," AND "),c("OR"," OR "),c("NOT","NOT "),c("BETWEEN"," BETWEEN  AND "),c("IS NULL"," IS NULL"),c("IS NOT NULL"," IS NOT NULL")];r==="value"&&b.unshift(c("CASE WHEN \u2026 END","CASE WHEN  THEN  ELSE  END"));let m=o("div",{class:"ir-condition"},z("Expression",n),o("div",{class:"ir-token-group"},o("span",{class:"ir-field-label"},"Columns"),o("div",{},...s.map(d=>c(d.label,d.name)))),o("div",{class:"ir-token-group"},o("span",{class:"ir-field-label"},"Functions"),o("div",{},...i.expressionFunctions().map(d=>c(d,`${d}(`)))),o("div",{class:"ir-token-group"},o("span",{class:"ir-field-label"},"Conditions"),o("div",{},...b)),o("p",{class:"ir-dialog-note"},r==="predicate"?"The expression must resolve to true or false. Strings use single quotes; dates use TO_DATE('YYYY-MM-DD').":"The expression must produce a number, text, or date value. Use CASE WHEN to turn conditions into values."));return m._read=()=>{let d=n.value.trim();if(!d)throw new Error(r==="predicate"?"Enter a condition expression":"Enter an expression");return d},m}function ke(i){let e=i.pickable(),t=new Map(e.map(d=>[d.name,d])),r=i.visibleColumnNames().filter(d=>t.has(d)),a=e.map(d=>d.name).filter(d=>!r.includes(d)),n=d=>o("select",{multiple:!0,size:12,class:"ir-shuttle-list"},...d.map(h=>new Option(t.get(h).computed?`\u0192 ${t.get(h).label}`:t.get(h).label,h))),s=n(a),l=n(r),c=(d,h,p)=>{let g=p?[...d.options]:[...d.selectedOptions];for(let v of g)h.append(v)},b=d=>{let h=[...l.selectedOptions];d>0&&h.reverse();for(let p of h){let g=d<0?p.previousElementSibling:p.nextElementSibling;!g||g.selected||(d<0?g.before(p):g.after(p))}},m=(d,h,p)=>o("button",{type:"button",class:"ir-btn",title:h,onclick:p},d);A({owner:i,title:"Select Columns",width:"34rem",build:d=>d.append(o("div",{class:"ir-shuttle"},o("div",{class:"ir-shuttle-col"},o("div",{class:"ir-shuttle-head"},"Do Not Display"),s),o("div",{class:"ir-shuttle-btns"},m("\u203A","Display selected",()=>c(s,l)),m("\u2039","Hide selected",()=>c(l,s)),m("\xBB","Display all",()=>c(s,l,!0)),m("\xAB","Hide all",()=>c(l,s,!0))),o("div",{class:"ir-shuttle-col"},o("div",{class:"ir-shuttle-head"},"Display in Report"),l),o("div",{class:"ir-shuttle-btns"},m("\u2191","Move up",()=>b(-1)),m("\u2193","Move down",()=>b(1))))),onApply:()=>{let d=[...l.options].map(h=>h.value);if(!d.length)throw new Error("Display at least one column");return i.apply(h=>{h.columns=d})}})}function W(i,{editIndex:e,col:t}={}){let r=e!==void 0?i.doc.filters?.[e]:void 0,a=K(i,{initial:r?.expr??(t?`${t} = `:""),placeholder:"e.g. AMOUNT > 1000 AND STATUS <> 'CANCELLED'",result:"predicate"});A({owner:i,title:e!==void 0?"Edit Filter":"Add Filter",width:"30rem",build:n=>n.append(a),onApply:()=>{let n={expr:a._read(),enabled:r?.enabled??!0};return i.apply(s=>{s.filters??=[],e!==void 0?s.filters[e]=n:s.filters.push(n)})}})}function Se(i){let e=o("div",{}),t=M(e,i.doc.sorts??[],(r,a)=>{let n=R(P(i,{none:"\u2014 Select \u2014"}),a?.col??""),s=R(we,a?.dir??"asc");r.append(n,s),r._read=()=>n.value?{col:n.value,dir:s.value}:null},{addLabel:"Sort",max:6});A({owner:i,title:"Sort",width:"26rem",build:r=>r.append(e,t.addButton,o("p",{class:"ir-dialog-note"},"Control-break columns always sort first.")),onApply:()=>i.apply(r=>{r.sorts=t.read()})})}function X(i){let e=o("div",{}),t=M(e,(i.doc.breaks??[]).map(r=>({col:r})),(r,a)=>{let n=R(P(i,{none:"\u2014 Select \u2014"}),a?.col??"");r.append(n),r._read=()=>n.value||null},{addLabel:"Break Column",max:3});A({owner:i,title:"Control Break",width:"24rem",build:r=>r.append(e,t.addButton,o("p",{class:"ir-dialog-note"},"Rows group under a heading per break value; aggregates subtotal per group.")),onApply:()=>i.apply(r=>{r.breaks=[...new Set(t.read())]})})}function J(i){let e=o("div",{}),t=M(e,i.doc.aggregates??[],(r,a)=>{let n=R(P(i,{none:"\u2014 Select \u2014"}),a?.col??""),s=o("select",{class:"ir-select"}),l=c=>{let b=n.value?i.fnsFor(i.typeOf(n.value)):[];s.replaceChildren(...b.map(m=>new Option(D[m]??m,m))),c&&b.includes(c)&&(s.value=c)};n.onchange=()=>l(s.value),l(a?.fn),r.append(s,o("span",{class:"ir-row-of"},"of"),n),r._read=()=>n.value&&s.value?{col:n.value,fn:s.value}:null},{addLabel:"Aggregate"});A({owner:i,title:"Aggregate",width:"28rem",build:r=>r.append(e,t.addButton,o("p",{class:"ir-dialog-note"},"Computed over the whole filtered set \u2014 grand total and per-break subtotals.")),onApply:()=>i.apply(r=>{r.aggregates=t.read()})})}function Z(i,e){let t=e!==void 0?i.doc.computed?.[e]:void 0,r=o("input",{class:"ir-input",type:"text",value:t?.label??"",placeholder:"Column heading"}),a=K(i,{initial:t?.expr,placeholder:"e.g. ROUND(AMOUNT * 1.0825, 2)",result:"value",columns:i.schema.columns});A({owner:i,title:e!==void 0?"Edit Computed Column":"Compute Column",width:"36rem",build:n=>n.append(z("Column Heading",r),a),onApply:()=>{let n=a._read(),s=(i.doc.computed??[]).map(b=>b.id),l=1;for(;s.includes(`c${l}`);)l++;let c={id:t?.id??`c${l}`,label:r.value.trim()||(t?.id??`c${l}`),expr:n,enabled:t?.enabled??!0};return i.apply(b=>{b.computed??=[],e!==void 0?b.computed[e]=c:b.computed.push(c)})}})}function ee(i,e){let t=e!==void 0?i.doc.highlights?.[e]:void 0,r=R([{value:"row",label:"Row"},{value:"cell",label:"Cell"}],t?.scope??"row"),a=R(P(i),t?.col),n=z("Highlight Column",a),s=K(i,{initial:t?.expr,placeholder:"e.g. ROUND(AMOUNT, 2) > 1000 OR NOTES IS NULL",result:"predicate"}),l=o("input",{type:"color",class:"ir-color",value:t?.style?.bg??"#fff3cd"}),c=o("input",{type:"checkbox",checked:t?!!t.style?.bg:!0}),b=o("input",{type:"color",class:"ir-color",value:t?.style?.fg??"#9f1239"}),m=o("input",{type:"checkbox",checked:!!t?.style?.fg}),d=()=>{n.hidden=r.value!=="cell"};r.onchange=d,d(),A({owner:i,title:e!==void 0?"Edit Highlight":"Highlight",width:"30rem",build:h=>h.append(z("Highlight",r),n,o("div",{class:"ir-field-label ir-condition-head"},"When"),s,o("div",{class:"ir-colors"},o("label",{class:"ir-color-pick"},c,"Background",l),o("label",{class:"ir-color-pick"},m,"Text",b))),onApply:()=>{let h=s._read();if(!c.checked&&!m.checked)throw new Error("Pick a background or text color");let p=(i.doc.highlights??[]).map(S=>S.id),g=1;for(;p.includes(`h${g}`);)g++;let v={id:t?.id??`h${g}`,enabled:t?.enabled??!0,scope:r.value,expr:h};return r.value==="cell"&&(v.col=a.value),v.style={},c.checked&&(v.style.bg=l.value),m.checked&&(v.style.fg=b.value),i.apply(S=>{S.highlights??=[],e!==void 0?S.highlights[e]=v:S.highlights.push(v)})}})}function Q(i,e,{addLabel:t,max:r}){let a=o("div",{}),n=M(a,(e??[]).map(s=>({col:s})),(s,l)=>{let c=R(P(i,{none:"\u2014 Select \u2014"}),l?.col??"");s.append(c),s._read=()=>c.value||null},{addLabel:t,max:r});return{container:a,list:n}}function Ce(i,e){let t=o("div",{}),r=M(t,e??[],(a,n)=>{let s=R(P(i,{none:"\u2014 Select \u2014"}),n?.col??""),l=o("select",{class:"ir-select"}),c=b=>{let m=s.value?i.fnsFor(i.typeOf(s.value)):[];l.replaceChildren(...m.map(d=>new Option(D[d]??d,d))),b&&m.includes(b)&&(l.value=b)};s.onchange=()=>c(l.value),c(n?.fn),a.append(l,o("span",{class:"ir-row-of"},"of"),s),a._read=()=>s.value&&l.value?{col:s.value,fn:l.value}:null},{addLabel:"Value"});return{container:t,list:r}}function te(i){let e=i.doc.view?.mode==="groupBy"?i.doc.view:i.viewMemory.groupBy,t=Q(i,e?.groupBy,{addLabel:"Group Column",max:3}),r=Ce(i,e?.values);A({owner:i,title:"Group By",width:"30rem",build:a=>a.append(o("div",{class:"ir-field-label"},"Group by"),t.container,t.list.addButton,o("div",{class:"ir-field-label ir-gap-above"},"Aggregate values"),r.container,r.list.addButton,o("p",{class:"ir-dialog-note"},"A row count per group is always included.")),onApply:()=>{let a=[...new Set(t.list.read())];if(!a.length)throw new Error("Pick at least one group column");let n={mode:"groupBy",groupBy:a,values:r.list.read()};return i.apply(s=>{s.view=n}).then(()=>{i.viewMemory.groupBy=n})}})}function re(i){let e=i.doc.view?.mode==="pivot"?i.doc.view:i.viewMemory.pivot,t=Q(i,e?.rows,{addLabel:"Row Column",max:2}),r=Q(i,e?.cols,{addLabel:"Column",max:2}),a=Ce(i,e?.values);A({owner:i,title:"Pivot",width:"30rem",build:n=>n.append(o("div",{class:"ir-field-label"},"Rows"),t.container,t.list.addButton,o("div",{class:"ir-field-label ir-gap-above"},"Columns (become headings)"),r.container,r.list.addButton,o("div",{class:"ir-field-label ir-gap-above"},"Values"),a.container,a.list.addButton,o("p",{class:"ir-dialog-note"},"No values = a count per cell.")),onApply:()=>{let n=[...new Set(t.list.read())],s=[...new Set(r.list.read())].filter(c=>!n.includes(c));if(!n.length||!s.length)throw new Error("Pick at least one row column and one distinct column heading");let l={mode:"pivot",rows:n,cols:s,values:a.list.read()};return i.apply(c=>{c.view=l}).then(()=>{i.viewMemory.pivot=l})}})}var Be=[{value:"bar",label:"Bar"},{value:"line",label:"Line"},{value:"area",label:"Line with Area"},{value:"pie",label:"Pie"}];function ie(i){let e=i.doc.view?.mode==="chart"?i.doc.view:i.viewMemory.chart,t=i.pickable().filter(x=>x.type!=="other"),r=R(Be,e?.type??"bar"),a=R([{value:"",label:"\u2014 Select \u2014"},...t.map(x=>({value:x.name,label:x.computed?`\u0192 ${x.label}`:x.label}))],e?.label??""),n=R([{value:"",label:"\u2014 Row Count \u2014"},...i.pickable().map(x=>({value:x.name,label:x.computed?`\u0192 ${x.label}`:x.label}))],e?.value??""),s=o("select",{class:"ir-select"}),l=x=>{let u=[];if(!n.value)u.push({value:"count",label:D.count});else{let f=i.typeOf(n.value);u.push(...i.chartFnsFor(f).map(y=>({value:y,label:D[y]??y}))),f==="number"&&u.push({value:"",label:"\u2014 Each Row \u2014"})}s.replaceChildren(...u.map(f=>new Option(f.label,f.value))),x!==void 0&&[...s.options].some(f=>f.value===x)&&(s.value=x)};n.onchange=()=>l(s.value),l(e?e.fn??"":void 0);let c=R([{value:"vertical",label:"Vertical"},{value:"horizontal",label:"Horizontal"}],e?.orientation??"vertical"),b=R([{value:"label",label:"Label"},{value:"value",label:"Value"}],e?.sort?.by??"label"),m=R(we,e?.sort?.dir??"asc"),d=o("input",{class:"ir-input",type:"text",value:e?.labelAxisTitle??"",placeholder:"Optional"}),h=o("input",{class:"ir-input",type:"text",value:e?.valueAxisTitle??"",placeholder:"Optional"}),p=z("Orientation",c),g=z("Label Axis Title",d),v=z("Value Axis Title",h),S=()=>{let x=r.value==="pie";p.hidden=x,g.hidden=x,v.hidden=x};r.onchange=S,S(),A({owner:i,title:"Chart",width:"30rem",build:x=>x.append(z("Chart Type",r),z("Label",a),o("div",{class:"ir-field"},o("span",{class:"ir-field-label"},"Value"),o("div",{class:"ir-dlgrow ir-chart-valuerow"},s,o("span",{class:"ir-row-of"},"of"),n)),p,o("div",{class:"ir-field"},o("span",{class:"ir-field-label"},"Sort"),o("div",{class:"ir-dlgrow"},b,m)),g,v,o("p",{class:"ir-dialog-note"},"The chart draws the whole filtered result \u2014 never just the visible page \u2014 up to the report's point limit.")),onApply:()=>{if(!a.value)throw new Error("Pick a label column");let x={mode:"chart",type:r.value,label:a.value,sort:{by:b.value,dir:m.value}};return n.value&&(x.value=n.value),s.value&&(x.fn=s.value),r.value!=="pie"&&(x.orientation=c.value,d.value.trim()&&(x.labelAxisTitle=d.value.trim()),h.value.trim()&&(x.valueAxisTitle=h.value.trim())),i.apply(u=>{u.view=x}).then(()=>{i.viewMemory.chart=x})}})}function ae(i,{asNew:e}){let t=!e&&i.canManageCurrentSaved(),r=o("input",{class:"ir-input",type:"text",maxLength:200,value:t?i.currentSaved.title:"",placeholder:"Saved report name"}),a=o("input",{type:"checkbox",checked:t?!!i.currentSaved.isGlobal:!1});A({owner:i,title:t?"Save Report":"Save Report As",width:"26rem",applyLabel:"Save",build:n=>{n.append(z("Name",r)),i.whoami?.isAdministrator&&n.append(o("label",{class:"ir-checkline"},a,"Global \u2014 visible to everyone with access to this report"))},onApply:()=>{let n=r.value.trim();if(!n)throw new Error("Enter a name");return i.saveReport({title:n,isGlobal:i.whoami?.isAdministrator?a.checked:!1,asNew:!t})}})}var $e=new URL("..",import.meta.url).pathname.replace(/\/$/,""),Ee=(i,e)=>typeof i=="string"&&typeof e=="string"&&i.toUpperCase()===e.toUpperCase(),Re,De=()=>Re??=import(new URL("./ir-chart.js",import.meta.url).href).catch(i=>{throw Re=void 0,i}),oe=class extends HTMLElement{static observedAttributes=["report","api-base","base"];constructor(){super();let{root:e,mount:t}=de(this);this._root=e,this._mount=t,this._seq=0,this._initialized=!1}get apiBase(){return this.getAttribute("api-base")??this.getAttribute("base")??$e}set apiBase(e){e==null?this.removeAttribute("api-base"):this.setAttribute("api-base",String(e))}get base(){return this.apiBase.replace(/\/+$/,"")}get requestedReportName(){return this.getAttribute("report")}get reportName(){return this._activeReportName??this.requestedReportName}connectedCallback(){this.scheduleInit()}disconnectedCallback(){++this._seq,this._abort?.abort(),this._abort=null,this.destroyChart(),pe(this)}attributeChangedCallback(e,t,r){this._initialized&&t!==r&&this.scheduleInit()}scheduleInit(){this._initQueued||(this._initQueued=!0,queueMicrotask(()=>{this._initQueued=!1,this.isConnected&&this.init()}))}async init(){let e=++this._seq;this._initialized=!0,this._abort?.abort(),this._abort=null,this.destroyChart(),this.schema=null,this.doc=null,this.lastResult=null,this.availableReports=[],this._activeReportName=null,this.whoami=null,this.savedList=[],this.currentSaved=null,this.searchScopeCol=null,this.viewMemory={},this.buildSkeleton();try{let[t,r]=await Promise.all([O(this.base),O(`${this.base}/whoami`).catch(()=>null)]);if(e!==this._seq)return;this.availableReports=t,this.whoami=r,this.refreshReportSelect();let a=this.requestedReportName,n=this.availableReports.find(l=>Ee(l.name,a)),s=n?[n,...this.availableReports.filter(l=>l!==n)]:this.availableReports;if(!s.length){this.showError(new Error("No reports are available for the current user."));return}for(let l of s)if(await this.activateReport(l.name,e,{quiet:!0})||e!==this._seq)return;this.showError(new Error("None of the reports available to the current user could be loaded."))}catch(t){t.name!=="AbortError"&&e===this._seq&&this.showError(t)}}refreshReportSelect(){let{reportSel:e,reportWrap:t}=this.els;e.replaceChildren(...this.availableReports.map(r=>new Option(r.title,r.name))),e.value=this._activeReportName??"",t.hidden=this.availableReports.length<=1}async activateReport(e,t=++this._seq,{quiet:r=!1}={}){let a=this.availableReports.find(n=>Ee(n.name,e));if(!a||t!==this._seq)return!1;this._abort?.abort(),this._abort=null,this._activeReportName=a.name,this.schema=null,this.doc=null,this.lastResult=null,this.savedList=[],this.currentSaved=null,this.searchScopeCol=null,this.viewMemory={},this.els.search.value="",this.destroyChart(),this.els.chartWrap.replaceChildren(),this.els.chartWrap.hidden=!0,this.els.tablewrap.hidden=!1,this.els.table.replaceChildren(),this.els.pager.replaceChildren(),this.els.chips.replaceChildren(),this.els.chips.hidden=!0,this.clearError(),this.refreshReportSelect(),this.refreshSavedSelect(),this._mount.classList.add("ir-busy");try{let n=await O(`${this.base}/${encodeURIComponent(a.name)}/schema`);if(t!==this._seq)return!1;let s=await O(`${this.base}/${encodeURIComponent(a.name)}/saved`).catch(()=>[]);return t!==this._seq?void 0:(this.schema=n,this.savedList=s,this.doc=this.normalize(n.defaultState),this.els.search.value=this.doc.search??"",this.refreshSavedSelect(),await this.runQuery({quiet:r}),t===this._seq&&this.lastResult!==null)}catch(n){return!r&&n.name!=="AbortError"&&t===this._seq&&this.showError(n),!1}finally{t===this._seq&&this._mount.classList.remove("ir-busy")}}buildSkeleton(){let e=o("button",{type:"button",class:"ir-btn ir-search-scope",title:"Choose search column","aria-label":"Choose search column",onclick:()=>this.openSearchScopeMenu(e)},B("search"),B("caret")),t=o("input",{class:"ir-search-input",type:"search",placeholder:"Search",onkeydown:d=>{d.key==="Enter"&&this.doSearch()}}),r=o("button",{type:"button",class:"ir-btn ir-go",onclick:()=>this.doSearch()},"Go"),a=(d,h,p)=>o("button",{type:"button",class:"ir-btn ir-viewbtn",dataset:{mode:d},title:p,"aria-label":p,onclick:()=>this.switchView(d)},B(h)),n=o("div",{class:"ir-viewbtns",role:"group","aria-label":"View"},a("grid","grid","Grid"),a("groupBy","group","Group By"),a("pivot","pivot","Pivot"),a("chart","chart","Chart")),s=o("button",{type:"button",class:"ir-btn ir-actionsbtn",onclick:()=>this.openActionsMenu(s)},"Actions",B("caret")),l=o("select",{class:"ir-select ir-saved-select",onchange:()=>l.value?this.loadSavedById(l.value):this.resetToPrimary()}),c=o("label",{class:"ir-saved",hidden:!0},o("span",{class:"ir-saved-label"},"Saved Report"),l),b=o("select",{class:"ir-select ir-report-select",part:"report-select",onchange:()=>this.activateReport(b.value)}),m=o("label",{class:"ir-saved",hidden:!0},o("span",{class:"ir-saved-label"},"Report"),b);this.els={search:t,views:n,reportSel:b,reportWrap:m,savedSel:l,savedWrap:c,errorSlot:o("div",{}),transientSlot:o("div",{}),ignoredSlot:o("div",{}),chips:o("div",{class:"ir-chips",part:"chips",hidden:!0}),table:o("table",{class:"ir-table",part:"table"}),chartWrap:o("div",{class:"ir-chartwrap",part:"chart-container",hidden:!0}),pager:o("div",{class:"ir-pager",part:"pager"})},this.els.tablewrap=o("div",{class:"ir-tablewrap",part:"table-container"},this.els.table),this._mount.replaceChildren(o("div",{class:"ir-toolbar",part:"toolbar"},o("div",{class:"ir-search"},e,t,r),n,s,o("span",{class:"ir-spacer"}),m,c),o("div",{class:"ir-busybar"}),o("div",{class:"ir-notices",part:"notices"},this.els.errorSlot,this.els.transientSlot,this.els.ignoredSlot),this.els.chips,this.els.tablewrap,this.els.chartWrap,this.els.pager)}pickable(){return this.lastResult?.availableColumns??this.schema?.columns??[]}typeOf(e){return this.pickable().find(t=>t.name===e)?.type??"other"}labelOf(e){return this.pickable().find(t=>t.name===e)?.label??e}fnsFor(e){let t=this.schema?.capabilities?.aggregateFunctions??{};return t[e]??t.other??[]}chartFnsFor(e){let t=this.schema?.capabilities?.chartAggregateFunctions??{};return t[e]??t.other??[]}expressionFunctions(){return this.schema?.capabilities?.expressionFunctions??[]}visibleColumnNames(){return this.doc?.columns?.length?[...this.doc.columns]:this.pickable().map(e=>e.name)}normalize(e){return ve(e,this.schema?.limits?.defaultPageSize??50,this.schema?.defaultState)}serialize(){return xe(this.doc,this.schema?.stateVersion??2)}async runQuery(e={}){this._abort?.abort();let t=this._abort=new AbortController;this._mount.classList.add("ir-busy");try{let r=await O(`${this.base}/${encodeURIComponent(this.reportName)}/query`,{method:"POST",body:this.serialize(),signal:t.signal});return t!==this._abort?void 0:(this.lastResult=r,this.clearError(),this.doc.view?.mode&&this.doc.view.mode!=="grid"&&(this.viewMemory[this.doc.view.mode]=this.doc.view),U(this,this.els.chips),this.renderView(),ge(this,this.els.pager),this.renderIgnored(r.ignored),this.refreshViewButtons(),r)}catch(r){if(r.name==="AbortError")return;throw U(this,this.els.chips),e.quiet||this.showError(r),r}finally{t===this._abort&&this._mount.classList.remove("ir-busy")}}renderView(){let e=(this.doc.view?.mode??"grid")==="chart";if(this.els.tablewrap.hidden=e,this.els.chartWrap.hidden=!e,!e){this.destroyChart(),this.els.chartWrap.replaceChildren(),he(this,this.els.table);return}this.els.table.replaceChildren(),this.renderChart()}async renderChart(){let e=this.lastResult;this.destroyChart();try{let t=await De();if(this.lastResult!==e||(this.doc.view?.mode??"grid")!=="chart"||!this.isConnected)return;this._chart=be(this,this.els.chartWrap,t)}catch{this.els.chartWrap.replaceChildren(),this.showError(new Error("The charting module failed to load. Reload the page and try again."))}}destroyChart(){this._chart?.destroy(),this._chart=null}async apply(e,{resetPage:t=!0}={}){let r=structuredClone(this.doc);e(this.doc),t&&this.doc.page&&(this.doc.page.index=1);try{await this.runQuery({quiet:!0})}catch(a){throw this.doc=r,U(this,this.els.chips),a}}applyOrBanner(e,t){return this.apply(e,t).catch(r=>this.showError(r))}showError(e){let t=e?.status===401?"Sign in to use this report.":e?.status===404?"Report not found \u2014 or you don't have access.":e?.message||String(e),r=e?.traceId?` (ref ${e.traceId})`:"";this.els.errorSlot.replaceChildren(F("error",t+r,()=>this.clearError()))}clearError(){this.els.errorSlot.replaceChildren()}notify(e,t="ok"){let r=F(t,e);this.els.transientSlot.append(r),setTimeout(()=>r.remove(),4e3)}renderIgnored(e){if(!e?.length){this.els.ignoredSlot.replaceChildren();return}let t="Some settings were ignored: "+e.map(r=>`${r.kind} (${r.detail})`).join("; ");this.els.ignoredSlot.replaceChildren(F("warn",t,()=>this.els.ignoredSlot.replaceChildren()))}doSearch(){let e=this.els.search.value.trim();if(!this.searchScopeCol){this.applyOrBanner(n=>{n.search=e});return}if(!e)return;let t=this.searchScopeCol,r=this.typeOf(t),a;try{a=ye(t,r,e)}catch(n){this.showError(n);return}this.els.search.value="",this.applyOrBanner(n=>{(n.filters??=[]).push({enabled:!0,expr:a})})}openSearchScopeMenu(e){let t=this.pickable().filter(r=>["text","number","date","bool"].includes(r.type));_(e,[{label:"All Text Columns",checked:!this.searchScopeCol,onPick:()=>this.setSearchScope(null)},"-",...t.map(r=>({label:r.label,checked:this.searchScopeCol===r.name,onPick:()=>this.setSearchScope(r.name)}))])}setSearchScope(e){this.searchScopeCol=e,this.els.search.placeholder=e?`Search: ${this.labelOf(e)}`:"Search",this.els.search.focus()}refreshViewButtons(){let e=this.doc?.view?.mode??"grid";for(let t of this.els.views.children)t.classList.toggle("ir-active",t.dataset.mode===e)}switchView(e){let t=this.doc.view?.mode??"grid";if(e===t)return;if(e==="grid"){this.applyOrBanner(a=>{a.view={mode:"grid"}});return}let r=this.viewMemory[e];r?this.applyOrBanner(a=>{a.view=r}):this.openViewDialog(e)}openViewDialog(e){e==="groupBy"?te(this):e==="pivot"?re(this):ie(this)}openActionsMenu(e){let t=this.canManageCurrentSaved();_(e,[{label:"Columns\u2026",onPick:()=>ke(this)},{label:"Filter\u2026",onPick:()=>W(this,{})},{label:"Sort\u2026",onPick:()=>Se(this)},"-",{label:"Control Break\u2026",onPick:()=>X(this)},{label:"Highlight\u2026",onPick:()=>ee(this)},{label:"Aggregate\u2026",onPick:()=>J(this)},{label:"Compute\u2026",onPick:()=>Z(this)},"-",{label:"Group By\u2026",onPick:()=>te(this)},{label:"Pivot\u2026",onPick:()=>re(this)},{label:"Chart\u2026",onPick:()=>ie(this)},{heading:"Report"},...t?[{label:"Save",onPick:()=>ae(this,{asNew:!1})}]:[],{label:"Save As\u2026",onPick:()=>ae(this,{asNew:!0})},...t?[{label:"Delete\u2026",onPick:()=>this.deleteCurrentSaved()}]:[],{label:"Reset",onPick:()=>this.resetWorkingCopy()},{heading:"Download"},{label:"CSV",onPick:()=>this.exportCsv()}])}openHeaderMenu(e,t){let r=this.doc.view?.mode??"grid",a=[{label:"Sort Ascending",onPick:()=>this.applyOrBanner(l=>{l.sorts=[{col:e,dir:"asc"}]})},{label:"Sort Descending",onPick:()=>this.applyOrBanner(l=>{l.sorts=[{col:e,dir:"desc"}]})}];if(r!=="grid"){_(t,a);return}let n=this.visibleColumnNames(),s=(this.doc.breaks??[]).includes(e);_(t,[...a,"-",{label:"Hide Column",disabled:n.length<=1,onPick:()=>this.applyOrBanner(l=>{l.columns=n.filter(c=>c!==e)})},{label:s?"Remove Control Break":"Control Break",checked:s,onPick:()=>this.applyOrBanner(l=>{l.breaks=s?(l.breaks??[]).filter(c=>c!==e):[...l.breaks??[],e]})},"-",{label:"Filter\u2026",onPick:()=>W(this,{col:e})}])}chipArray(e,t){return{filter:e.filters,aggregate:e.aggregates,computed:e.computed,highlight:e.highlights}[t]}chipToggle(e,t,r){this.applyOrBanner(a=>{if(e!=="filter"&&e!=="computed"&&e!=="highlight")return;let n=this.chipArray(a,e)?.[t];n&&(n.enabled=r)})}chipRemove(e,t){this.applyOrBanner(r=>{switch(e){case"search":r.search="",this.els.search.value="";break;case"break":{r.breaks=(r.breaks??[]).filter((a,n)=>n!==t);break}case"view":r.view={mode:"grid"};break;default:this.chipArray(r,e)?.splice(t,1)}})}chipEdit(e,t){switch(e){case"search":this.els.search.focus(),this.els.search.select();break;case"filter":W(this,{editIndex:t});break;case"break":X(this);break;case"aggregate":J(this);break;case"computed":Z(this,t);break;case"highlight":ee(this,t);break;case"view":this.openViewDialog(this.doc.view?.mode??"groupBy");break}}gotoPage(e){this.applyOrBanner(t=>{t.page.index=e},{resetPage:!1})}setPageSize(e){this.applyOrBanner(t=>{t.page.size=e})}canManageCurrentSaved(){let e=this.currentSaved;return e?this.whoami?.isAdministrator||e.mine&&!e.isGlobal:!1}refreshSavedSelect(){let{savedSel:e,savedWrap:t}=this.els;e.replaceChildren(new Option("Primary Report",""));let r=(a,n)=>{if(!n.length)return;let s=o("optgroup",{label:a});for(let l of n)s.append(new Option(l.title+(l.mine||l.isGlobal?"":` (${l.owner})`),l.id));e.append(s)};r("Global",this.savedList.filter(a=>a.isGlobal)),r("Private",this.savedList.filter(a=>!a.isGlobal)),e.value=this.currentSaved?.id??"",t.hidden=this.savedList.length===0}async loadSavedList(){this.savedList=await O(`${this.base}/${encodeURIComponent(this.reportName)}/saved`).catch(()=>[])}async loadSavedById(e){try{let t=await O(`${this.base}/saved/${encodeURIComponent(e)}`);this.currentSaved=t.summary,this.doc=this.normalize(t.state),this.els.search.value=this.doc.search??"",this.refreshSavedSelect(),await this.runQuery()}catch(t){t.name!=="AbortError"&&this.showError(t)}}resetToPrimary(){this.currentSaved=null,this.doc=this.normalize(this.schema?.defaultState),this.els.search.value=this.doc.search??"",this.refreshSavedSelect(),this.runQuery().catch(()=>{})}async resetWorkingCopy(){let e=this.currentSaved?`"${this.currentSaved.title}"`:"its default settings";await G(this,"Reset",`Restore this report to ${e}? Unsaved changes are lost.`,"Reset")&&(this.currentSaved?await this.loadSavedById(this.currentSaved.id):this.resetToPrimary())}async saveReport({title:e,isGlobal:t,asNew:r}){let a=this.serialize();if(r)this.currentSaved=await O(`${this.base}/${encodeURIComponent(this.reportName)}/saved`,{method:"POST",body:{title:e,state:a,isGlobal:t}});else{let n={title:e,state:a};this.whoami?.isAdministrator&&(n.isGlobal=t),this.currentSaved=await O(`${this.base}/saved/${encodeURIComponent(this.currentSaved.id)}`,{method:"PUT",body:n})}await this.loadSavedList(),this.refreshSavedSelect(),this.notify("Report saved.")}async deleteCurrentSaved(){let e=this.currentSaved;if(e&&await G(this,"Delete Saved Report",`Delete "${e.title}"? This cannot be undone.`))try{await O(`${this.base}/saved/${encodeURIComponent(e.id)}`,{method:"DELETE"}),this.currentSaved=null,await this.loadSavedList(),this.resetToPrimary(),this.notify("Saved report deleted.")}catch(t){this.showError(t)}}async exportCsv(){try{let{blob:e,filename:t,truncated:r}=await se(`${this.base}/${encodeURIComponent(this.reportName)}/export?format=csv`,this.serialize());le(e,t??`${this.reportName}.csv`),r&&this.notify("Export truncated at the report's row cap.","warn")}catch(e){this.showError(e)}}};customElements.get("interactive-report")||customElements.define("interactive-report",oe);
//# sourceMappingURL=ir.js.map
