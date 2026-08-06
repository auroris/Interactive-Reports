var G=class extends Error{constructor(t,e){let r=[];t.title&&r.push(t.title),t.detail&&r.push(t.detail);for(let o of Object.values(t.errors??{}))for(let n of o)r.push(n);super(r.join(" \u2014 ")||`HTTP ${e}`),this.name="ApiError",this.status=e,this.problem=t,this.errors=t.errors??null,this.traceId=t.traceId??null}};async function oe(i){let t=await i.json().catch(()=>({}));return new G(t,i.status)}async function R(i,{method:t="GET",body:e,signal:r}={}){let o=await fetch(i,{method:t,signal:r,headers:e!==void 0?{"Content-Type":"application/json"}:void 0,body:e!==void 0?JSON.stringify(e):void 0});if(!o.ok)throw await oe(o);return o.status===204?null:o.json()}async function ne(i,t){let e=await fetch(i,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(t)});if(!e.ok)throw await oe(e);let r=e.headers.get("Content-Disposition")??"",o=/filename="?([^";]+)"?/.exec(r)?.[1]??null;return{blob:await e.blob(),filename:o,truncated:e.headers.get("X-IR-Truncated")==="true"}}function se(i,t){let e=document.createElement("a");e.href=URL.createObjectURL(i),e.download=t,e.click(),URL.revokeObjectURL(e.href)}var ae=`/* InteractiveReport widget theme \u2014 an APEX Universal-Theme-flavored skin.
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
`;function s(i,t={},...e){let r=document.createElement(i);for(let[o,n]of Object.entries(t))n!=null&&(o==="class"?r.className=n:o==="part"?r.setAttribute("part",n):o==="for"?r.htmlFor=n:o==="dataset"?Object.assign(r.dataset,n):o==="style"?Object.assign(r.style,n):o.startsWith("on")||o in r?r[o]=n:r.setAttribute(o,n));return r.append(...e.flat(1/0).filter(o=>o!=null&&o!==!1)),r}function le(i){let t=i.attachShadow({mode:"open"}),e=s("style",{"data-ir-styles":""});e.textContent=ae;let r=s("div",{part:"surface"});return t.append(e,r),{root:t,mount:r}}var ke={search:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><circle cx="6.5" cy="6.5" r="4.5" fill="none" stroke="currentColor" stroke-width="1.6"/><line x1="10" y1="10" x2="14" y2="14" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',caret:'<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 5.5 8 11l5-5.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',grid:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M1.5 9.5h13 M6 2.5v11 M11 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>',group:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><rect x="1.5" y="2" width="6" height="3" fill="currentColor" opacity=".55"/><rect x="1.5" y="6.5" width="10" height="3" fill="currentColor" opacity=".8"/><rect x="1.5" y="11" width="13" height="3" fill="currentColor"/></svg>',pivot:'<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M1.5 2.5h13v11h-13z M1.5 6h13 M6 2.5v11" fill="none" stroke="currentColor" stroke-width="1.2"/><circle cx="10.5" cy="10" r="1.4" fill="currentColor"/></svg>',close:'<svg viewBox="0 0 16 16" width="10" height="10" aria-hidden="true"><path d="M3 3l10 10M13 3L3 13" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>'};function O(i){let t=s("span",{class:"ir-icon","aria-hidden":"true"});return t.innerHTML=ke[i]??"",t}function j(i,t,e){return s("div",{class:`ir-banner ir-banner-${i}`},s("span",{class:"ir-banner-text"},t),e?s("button",{type:"button",class:"ir-banner-x","aria-label":"Dismiss",onclick:e},O("close")):null)}var V=null,Q=null,I=new WeakMap;function N(){V?.(),V=null,Q=null}function ce(i){Q===i&&N();for(let t of[...I.get(i)??[]])t.close();I.delete(i)}function _(i,t){N();let e=s("div",{class:"ir-popup",part:"menu",role:"menu"}),r=i.getRootNode(),o=r instanceof ShadowRoot?r:document.body;Q=r instanceof ShadowRoot?r.host:null;for(let p of t){if(p==="-"){e.append(s("div",{class:"ir-menu-sep",role:"separator"}));continue}if(p.heading!==void 0){e.append(s("div",{class:"ir-menu-heading"},p.heading));continue}let v=s("button",{type:"button",class:"ir-menu-item"+(p.checked?" ir-checked":""),role:"menuitem",disabled:p.disabled===!0,onclick:()=>{N(),p.onPick?.()}},s("span",{class:"ir-menu-check","aria-hidden":"true"},p.checked?"\u2713":""),s("span",{class:"ir-menu-label"},p.label),p.hint?s("span",{class:"ir-menu-hint"},p.hint):null);e.append(v)}o.append(e);let n=i.getBoundingClientRect();e.style.position="fixed",e.style.visibility="hidden",e.style.left="0",e.style.top="0";let a=e.getBoundingClientRect(),l=Math.min(n.left,window.innerWidth-a.width-8),c=n.bottom+2;c+a.height>window.innerHeight-8&&(c=Math.max(8,n.top-a.height-2)),e.style.left=`${Math.max(8,l)}px`,e.style.top=`${c}px`,e.style.visibility="";let f=p=>{let v=p.composedPath?.()??[p.target];!v.includes(e)&&!v.includes(i)&&N()},b=p=>{if(p.key==="Escape"){N(),i.focus?.();return}if(p.key!=="ArrowDown"&&p.key!=="ArrowUp"&&p.key!=="Home"&&p.key!=="End")return;let v=[...e.querySelectorAll(".ir-menu-item:not([disabled])")];if(!v.length)return;p.preventDefault();let m=v.indexOf(r.activeElement??document.activeElement),k=p.key==="Home"?0:p.key==="End"?v.length-1:p.key==="ArrowDown"?(m+1)%v.length:(m-1+v.length)%v.length;v[k].focus()},d=!1;requestAnimationFrame(()=>{d=!0});let u=()=>{d&&N()};return document.addEventListener("mousedown",f,!0),document.addEventListener("keydown",b,!0),window.addEventListener("scroll",u,!0),window.addEventListener("resize",u),V=()=>{document.removeEventListener("mousedown",f,!0),document.removeEventListener("keydown",b,!0),window.removeEventListener("scroll",u,!0),window.removeEventListener("resize",u),e.remove()},e.querySelector(".ir-menu-item:not([disabled])")?.focus(),e}function E({owner:i,title:t,width:e,cls:r,build:o,applyLabel:n="Apply",onApply:a,destructive:l=!1}){N();let c=i?.shadowRoot??document,f=c instanceof ShadowRoot?c:document.body,b=c.activeElement??document.activeElement,d=s("div",{class:"ir-dialog-error",hidden:!0}),u=s("div",{class:"ir-dialog-body"}),p=null;i&&(p=I.get(i)??new Set,I.set(i,p));let v=!1,m={root:null,body:u,close(){v||(v=!0,m.root.remove(),document.removeEventListener("keydown",h,!0),p?.delete(m),b?.focus?.())},setError(g){if(d.replaceChildren(),g==null){d.hidden=!0;return}let x=[];if(g.errors&&typeof g.errors=="object"){g.problem?.title&&x.push(g.problem.title);for(let S of Object.values(g.errors))x.push(...S)}else x.push(typeof g=="string"?g:g.message||"Something went wrong.");d.append(...x.map(S=>s("div",{},S))),d.hidden=!1}},k=a?s("button",{type:"button",class:"ir-btn ir-btn-primary"+(l?" ir-btn-danger":""),onclick:async()=>{m.setError(null);let g=m.root.querySelectorAll(".ir-dialog-footer button");g.forEach(x=>x.disabled=!0);try{await a(m),m.close()}catch(x){m.setError(x)}finally{g.forEach(x=>x.disabled=!1)}}},n):null,P=s("button",{type:"button",class:"ir-btn",onclick:()=>m.close()},a?"Cancel":"Close");m.root=s("div",{class:"ir-overlay"+(r?` ${r}`:""),part:"dialog-overlay"},s("div",{class:"ir-dialog",part:"dialog",role:"dialog","aria-modal":"true",style:e?{width:e}:{}},s("div",{class:"ir-dialog-title"},t,s("button",{type:"button",class:"ir-dialog-x","aria-label":"Close",onclick:()=>m.close()},O("close"))),d,u,s("div",{class:"ir-dialog-footer"},P,k)));let h=g=>{if(g.key==="Escape"){g.stopPropagation(),m.close();return}let x=g.composedPath?.()[0]??g.target;if(g.key==="Enter"&&k&&x.tagName!=="TEXTAREA"&&x.tagName!=="BUTTON"){g.preventDefault(),k.click();return}if(g.key!=="Tab")return;let S=[...m.root.querySelectorAll("button, input, select, textarea, [tabindex]")].filter(z=>!z.disabled&&z.offsetParent!==null);if(!S.length)return;let C=S[0],y=S[S.length-1],w=c.activeElement??document.activeElement;g.shiftKey&&w===C?(g.preventDefault(),y.focus()):!g.shiftKey&&w===y&&(g.preventDefault(),C.focus())};return document.addEventListener("keydown",h,!0),o(u,m),f.append(m.root),p?.add(m),(u.querySelector("input, select, textarea")??k??P).focus(),m}function Y(i,t,e,r="Delete"){return new Promise(o=>{let n=!1,a=E({owner:i,title:t,width:"26rem",applyLabel:r,destructive:!0,build:c=>c.append(s("p",{class:"ir-confirm-text"},e)),onApply:()=>{n=!0}}),l=a.close;a.close=()=>{l(),o(n)}})}function D(i,t,e={}){return s("label",{class:"ir-field"+(e.inline?" ir-field-inline":"")},s("span",{class:"ir-field-label"},i),t)}function B(i,t){let e=s("select",{class:"ir-select"}),r=o=>typeof o=="string"?new Option(o,o):new Option(o.label,o.value);for(let o of i)if(o.options){let n=s("optgroup",{label:o.label});n.append(...o.options.map(r)),e.append(n)}else e.append(r(o));return t!=null&&(e.value=t),e}function Se(i,t,e=!1){if(i==null)return"";if(typeof i=="boolean")return i?"true":"false";if(t==="number"&&typeof i=="number")return!e&&Number.isInteger(i)?String(i):i.toLocaleString(void 0,{minimumFractionDigits:2,maximumFractionDigits:2});if(t==="date"){let r=String(i);return r.endsWith("T00:00:00")?r.slice(0,10):r.replace("T"," ")}return String(i)}function de(i){return i==null?"\u2014":typeof i=="number"?i.toLocaleString(void 0,{maximumFractionDigits:2}):String(i)}var T={sum:"Sum",avg:"Avg",min:"Min",max:"Max",count:"Count",countDistinct:"Count Distinct"},Ce=["sum","avg","min","max","count","countDistinct"];function A({w:i,kind:t,index:e,text:r,colLabel:o,off:n,toggleable:a=!0,removable:l=!0,swatch:c}){let f=s("span",{class:"ir-chip"+(n?" ir-chip-off":""),dataset:{kind:t}});a&&f.append(s("input",{type:"checkbox",class:"ir-chip-check",checked:!n,title:n?"Enable":"Disable",onchange:d=>i.chipToggle(t,e,d.target.checked)})),c&&f.append(s("span",{class:"ir-chip-swatch",style:{background:c}}));let b=s("button",{type:"button",class:"ir-chip-label",title:"Edit",onclick:()=>i.chipEdit(t,e)});return o&&b.append(s("b",{},o)," "),b.append(r),f.append(b),l&&f.append(s("button",{type:"button",class:"ir-chip-x","aria-label":"Remove",title:"Remove",onclick:()=>i.chipRemove(t,e)},O("close"))),f}function q(i,t){let e=i.doc,r=[];e.search&&r.push(A({w:i,kind:"search",index:0,toggleable:!1,colLabel:"Search",text:`'${e.search}'`})),(e.filters??[]).forEach((o,n)=>r.push(A({w:i,kind:"filter",index:n,off:o.enabled===!1,colLabel:"Filter",text:o.expr}))),(e.breaks??[]).forEach((o,n)=>r.push(A({w:i,kind:"break",index:n,toggleable:!1,colLabel:"Break",text:i.labelOf(o)}))),(e.aggregates??[]).forEach((o,n)=>r.push(A({w:i,kind:"aggregate",index:n,toggleable:!1,colLabel:"\u03A3",text:`${T[o.fn]??o.fn} of ${i.labelOf(o.col)}`}))),(e.computed??[]).forEach((o,n)=>r.push(A({w:i,kind:"computed",index:n,off:o.enabled===!1,colLabel:"\u0192",text:o.label??o.id}))),(e.highlights??[]).forEach((o,n)=>r.push(A({w:i,kind:"highlight",index:n,off:o.enabled===!1,swatch:o.style?.bg??"#fff3a0",colLabel:"Highlight",text:o.expr+(o.scope==="cell"?` (${i.labelOf(o.col)} cell)`:" (row)")}))),e.view?.mode==="groupBy"?r.push(A({w:i,kind:"view",index:0,toggleable:!1,colLabel:"Group by",text:(e.view.groupBy??[]).map(o=>i.labelOf(o)).join(", ")})):e.view?.mode==="pivot"&&r.push(A({w:i,kind:"view",index:0,toggleable:!1,colLabel:"Pivot",text:`${(e.view.rows??[]).map(o=>i.labelOf(o)).join(", ")} \xD7 ${(e.view.cols??[]).map(o=>i.labelOf(o)).join(", ")}`})),t.replaceChildren(...r),t.hidden=r.length===0}function pe(i,t){let e=i.lastResult;if(!e){t.replaceChildren();return}let r=i.doc.view?.mode??"grid",o=e.columns,n=new Map((i.doc.sorts??[]).map((h,g)=>[h.col,{dir:h.dir??"asc",ord:g+1}])),a=r==="groupBy"?new Set(i.doc.view?.groupBy??[]):null,l=s("tr",{});for(let h of o){let g=r==="grid"||r==="groupBy"&&a.has(h.name),x=n.get(h.name),S=s("span",{class:"ir-th-inner"},h.label);x&&(S.append(s("span",{class:"ir-sort-dir","aria-hidden":"true"},x.dir==="desc"?"\u25BC":"\u25B2")),(i.doc.sorts??[]).length>1&&S.append(s("span",{class:"ir-sort-ord"},String(x.ord))));let C=s("th",{class:(h.type==="number"?"ir-num ":"")+(g?"ir-th-menu":""),scope:"col","aria-sort":x?x.dir==="desc"?"descending":"ascending":void 0},S);g&&(C.onclick=()=>i.openHeaderMenu(h.name,C)),l.append(C)}let c=r==="grid"?i.doc.breaks??[]:[],f=h=>c.map(g=>String(h[g]??"")).join(""),b=new Map((e.breakTotals??[]).map(h=>[f(h.key),h])),d=new Set(o.filter(h=>h.type==="number"&&e.rows.some(g=>typeof g[h.name]=="number"&&!Number.isInteger(g[h.name]))).map(h=>h.name)),u=new Map((i.doc.highlights??[]).map(h=>[h.id,h.style??{}])),p=new Map;for(let h of e.highlights??[])p.has(h.row)||p.set(h.row,[]),p.get(h.row).push(h);let v=(h,g)=>{let x=[],S=Ce.filter(C=>Object.values(h??{}).some(y=>C in y));for(let C of S){let y=s("tr",{class:g});o.forEach((w,z)=>{let L=h[w.name]&&C in h[w.name],U=s("td",{class:w.type==="number"?"ir-num":""});z===0?(U.append(s("span",{class:"ir-agg-fn"},`${T[C]??C}:`)),L&&U.append(" ",de(h[w.name][C]))):L&&(U.textContent=de(h[w.name][C])),y.append(U)}),x.push(y)}return x},m=[],k=null,P=()=>{if(k===null)return;let h=b.get(k);h&&Object.keys(h.aggregates??{}).length&&m.push(...v(h.aggregates,"ir-break-total"))};for(let[h,g]of e.rows.entries()){if(c.length){let y=f(g);if(y!==k){P();let w=b.get(y),z=c.map(L=>`${i.labelOf(L)}: ${g[L]??"(blank)"}`).join("  \xB7  ");m.push(s("tr",{class:"ir-break-header"},s("td",{colSpan:o.length},s("span",{},z),w?s("span",{class:"ir-break-count"},`${Number(w.rows).toLocaleString()} rows`):null))),k=y}}let x=s("tr",{class:"ir-row"});for(let y of o){let w=[y.type==="number"?"ir-num":"",y.type==="date"?"ir-date":""].join(" ").trim();x.append(s("td",{class:w||void 0},Se(g[y.name],y.type,d.has(y.name))))}let S=(p.get(h)??[]).filter(y=>!y.col),C=(p.get(h)??[]).filter(y=>!!y.col);for(let y of[...S,...C]){let w=u.get(y.id)??{};if(!y.col)w.bg&&(x.style.background=w.bg),w.fg&&(x.style.color=w.fg);else{let z=o.findIndex(L=>L.name===y.col);z>=0&&(w.bg&&(x.children[z].style.background=w.bg),w.fg&&(x.children[z].style.color=w.fg))}}m.push(x)}P(),Object.keys(e.aggregates??{}).length&&m.push(...v(e.aggregates,"ir-grand-total")),e.rows.length||m.push(s("tr",{class:"ir-empty"},s("td",{colSpan:Math.max(o.length,1)},"No data found."))),t.replaceChildren(s("thead",{},l),s("tbody",{},...m))}function ue(i,t){let e=i.lastResult;if(!e){t.replaceChildren();return}let{index:r,size:o}=e.page,n=e.totalRows,a=(i.doc.view?.mode??"grid")==="groupBy"?"groups":"rows",l=n===0?0:(r-1)*o+1,c=n===0?0:l+e.rows.length-1,f=Math.max(1,Math.ceil(n/o)),b=[...new Set([15,25,50,100,o])].filter(u=>u<=(i.schema?.limits?.maxPageSize??1/0)).sort((u,p)=>u-p),d=s("select",{class:"ir-select ir-pagesize",title:"Rows per page"},...b.map(u=>new Option(String(u),String(u))));d.value=String(o),d.onchange=()=>i.setPageSize(Number(d.value)),t.replaceChildren(s("div",{class:"ir-pager-left"},s("button",{type:"button",class:"ir-btn ir-page-btn",disabled:r<=1,"aria-label":"Previous page",onclick:()=>i.gotoPage(r-1)},"\u2039"),s("span",{class:"ir-page-info"},n===0?`0 ${a}`:`${l.toLocaleString()} \u2013 ${c.toLocaleString()} of ${Number(n).toLocaleString()} ${a}`),s("button",{type:"button",class:"ir-btn ir-page-btn",disabled:r>=f,"aria-label":"Next page",onclick:()=>i.gotoPage(r+1)},"\u203A"),s("span",{class:"ir-pagesize-wrap"},"Rows ",d)),s("div",{class:"ir-pager-right"},`${e.elapsedMs} ms`))}function fe(i,t=50,e=null){let r=e?structuredClone(e):{};for(let[o,n]of Object.entries(i?structuredClone(i):{}))n!=null&&(r[o]=n);return r.filters??=[],r.sorts??=[],r.page={index:1,size:r.page?.size??t},r}function ge(i,t){let e=r=>{if(Array.isArray(r))return r.map(e);if(r&&typeof r=="object"){let o={};for(let[n,a]of Object.entries(r))n.startsWith("_")||a===void 0||(o[n]=e(a));return o}return r};return{...e(i),v:t}}function be(i,t,e){let r=e.trim();if(!r)throw new Error("Enter a search value");switch(t){case"text":return`CONTAINS(${i}, ${he(r)})`;case"number":if(!/^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$/.test(r))throw new Error(`'${r}' is not a number`);return`${i} = ${r}`;case"date":if(!/^\d{4}-\d{2}-\d{2}$/.test(r))throw new Error(`'${r}' is not an ISO date (YYYY-MM-DD)`);return`${i} = TO_DATE(${he(r)})`;case"bool":{let o=r.toLowerCase();if(o==="true"||o==="1")return i;if(o==="false"||o==="0")return`NOT ${i}`;throw new Error(`'${r}' is not true or false`)}default:throw new Error(`Column '${i}' does not support scoped search`)}}function he(i){return`'${i.replaceAll("'","''")}'`}var Ee=[{value:"asc",label:"Ascending"},{value:"desc",label:"Descending"}];function $(i,{none:t}={}){let e=i.pickable().map(r=>({value:r.name,label:r.computed?`\u0192 ${r.label}`:r.label}));return t?[{value:"",label:t},...e]:e}function M(i,t,e,{addLabel:r="Add",max:o}={}){let n=l=>{if(o&&i.querySelectorAll(".ir-dlgrow").length>=o)return;let c=s("div",{class:"ir-dlgrow"});e(c,l),c.append(s("button",{type:"button",class:"ir-btn ir-row-x",title:"Remove","aria-label":"Remove row",onclick:()=>c.remove()},"\xD7")),i.append(c)};return t.forEach(n),t.length===0&&n(null),{addButton:s("button",{type:"button",class:"ir-btn ir-add-row",onclick:()=>n(null)},`+ ${r}`),read:()=>[...i.querySelectorAll(".ir-dlgrow")].map(l=>l._read()).filter(l=>l!=null)}}function X(i,{initial:t,placeholder:e,result:r,columns:o}){let n=s("textarea",{class:"ir-textarea",rows:r==="predicate"?4:3,spellcheck:!1,placeholder:e});n.value=t??"";let a=o??i.pickable(),l=d=>{let u=n.selectionStart??n.value.length;n.setRangeText(d,u,n.selectionEnd??u,"end"),n.focus()},c=(d,u)=>s("button",{type:"button",class:"ir-token",onclick:()=>l(u)},d),f=[c("="," = "),c("\u2260"," <> "),c("<"," < "),c("\u2264"," <= "),c(">"," > "),c("\u2265"," >= "),c("AND"," AND "),c("OR"," OR "),c("NOT","NOT "),c("BETWEEN"," BETWEEN  AND "),c("IS NULL"," IS NULL"),c("IS NOT NULL"," IS NOT NULL")];r==="value"&&f.unshift(c("CASE WHEN \u2026 END","CASE WHEN  THEN  ELSE  END"));let b=s("div",{class:"ir-condition"},D("Expression",n),s("div",{class:"ir-token-group"},s("span",{class:"ir-field-label"},"Columns"),s("div",{},...a.map(d=>c(d.label,d.name)))),s("div",{class:"ir-token-group"},s("span",{class:"ir-field-label"},"Functions"),s("div",{},...i.expressionFunctions().map(d=>c(d,`${d}(`)))),s("div",{class:"ir-token-group"},s("span",{class:"ir-field-label"},"Conditions"),s("div",{},...f)),s("p",{class:"ir-dialog-note"},r==="predicate"?"The expression must resolve to true or false. Strings use single quotes; dates use TO_DATE('YYYY-MM-DD').":"The expression must produce a number, text, or date value. Use CASE WHEN to turn conditions into values."));return b._read=()=>{let d=n.value.trim();if(!d)throw new Error(r==="predicate"?"Enter a condition expression":"Enter an expression");return d},b}function me(i){let t=i.pickable(),e=new Map(t.map(d=>[d.name,d])),r=i.visibleColumnNames().filter(d=>e.has(d)),o=t.map(d=>d.name).filter(d=>!r.includes(d)),n=d=>s("select",{multiple:!0,size:12,class:"ir-shuttle-list"},...d.map(u=>new Option(e.get(u).computed?`\u0192 ${e.get(u).label}`:e.get(u).label,u))),a=n(o),l=n(r),c=(d,u,p)=>{let v=p?[...d.options]:[...d.selectedOptions];for(let m of v)u.append(m)},f=d=>{let u=[...l.selectedOptions];d>0&&u.reverse();for(let p of u){let v=d<0?p.previousElementSibling:p.nextElementSibling;!v||v.selected||(d<0?v.before(p):v.after(p))}},b=(d,u,p)=>s("button",{type:"button",class:"ir-btn",title:u,onclick:p},d);E({owner:i,title:"Select Columns",width:"34rem",build:d=>d.append(s("div",{class:"ir-shuttle"},s("div",{class:"ir-shuttle-col"},s("div",{class:"ir-shuttle-head"},"Do Not Display"),a),s("div",{class:"ir-shuttle-btns"},b("\u203A","Display selected",()=>c(a,l)),b("\u2039","Hide selected",()=>c(l,a)),b("\xBB","Display all",()=>c(a,l,!0)),b("\xAB","Hide all",()=>c(l,a,!0))),s("div",{class:"ir-shuttle-col"},s("div",{class:"ir-shuttle-head"},"Display in Report"),l),s("div",{class:"ir-shuttle-btns"},b("\u2191","Move up",()=>f(-1)),b("\u2193","Move down",()=>f(1))))),onApply:()=>{let d=[...l.options].map(u=>u.value);if(!d.length)throw new Error("Display at least one column");return i.apply(u=>{u.columns=d})}})}function F(i,{editIndex:t,col:e}={}){let r=t!==void 0?i.doc.filters?.[t]:void 0,o=X(i,{initial:r?.expr??(e?`${e} = `:""),placeholder:"e.g. AMOUNT > 1000 AND STATUS <> 'CANCELLED'",result:"predicate"});E({owner:i,title:t!==void 0?"Edit Filter":"Add Filter",width:"30rem",build:n=>n.append(o),onApply:()=>{let n={expr:o._read(),enabled:r?.enabled??!0};return i.apply(a=>{a.filters??=[],t!==void 0?a.filters[t]=n:a.filters.push(n)})}})}function ve(i){let t=s("div",{}),e=M(t,i.doc.sorts??[],(r,o)=>{let n=B($(i,{none:"\u2014 Select \u2014"}),o?.col??""),a=B(Ee,o?.dir??"asc");r.append(n,a),r._read=()=>n.value?{col:n.value,dir:a.value}:null},{addLabel:"Sort",max:6});E({owner:i,title:"Sort",width:"26rem",build:r=>r.append(t,e.addButton,s("p",{class:"ir-dialog-note"},"Control-break columns always sort first.")),onApply:()=>i.apply(r=>{r.sorts=e.read()})})}function J(i){let t=s("div",{}),e=M(t,(i.doc.breaks??[]).map(r=>({col:r})),(r,o)=>{let n=B($(i,{none:"\u2014 Select \u2014"}),o?.col??"");r.append(n),r._read=()=>n.value||null},{addLabel:"Break Column",max:3});E({owner:i,title:"Control Break",width:"24rem",build:r=>r.append(t,e.addButton,s("p",{class:"ir-dialog-note"},"Rows group under a heading per break value; aggregates subtotal per group.")),onApply:()=>i.apply(r=>{r.breaks=[...new Set(e.read())]})})}function Z(i){let t=s("div",{}),e=M(t,i.doc.aggregates??[],(r,o)=>{let n=B($(i,{none:"\u2014 Select \u2014"}),o?.col??""),a=s("select",{class:"ir-select"}),l=c=>{let f=n.value?i.fnsFor(i.typeOf(n.value)):[];a.replaceChildren(...f.map(b=>new Option(T[b]??b,b))),c&&f.includes(c)&&(a.value=c)};n.onchange=()=>l(a.value),l(o?.fn),r.append(a,s("span",{class:"ir-row-of"},"of"),n),r._read=()=>n.value&&a.value?{col:n.value,fn:a.value}:null},{addLabel:"Aggregate"});E({owner:i,title:"Aggregate",width:"28rem",build:r=>r.append(t,e.addButton,s("p",{class:"ir-dialog-note"},"Computed over the whole filtered set \u2014 grand total and per-break subtotals.")),onApply:()=>i.apply(r=>{r.aggregates=e.read()})})}function ee(i,t){let e=t!==void 0?i.doc.computed?.[t]:void 0,r=s("input",{class:"ir-input",type:"text",value:e?.label??"",placeholder:"Column heading"}),o=X(i,{initial:e?.expr,placeholder:"e.g. ROUND(AMOUNT * 1.0825, 2)",result:"value",columns:i.schema.columns});E({owner:i,title:t!==void 0?"Edit Computed Column":"Compute Column",width:"36rem",build:n=>n.append(D("Column Heading",r),o),onApply:()=>{let n=o._read(),a=(i.doc.computed??[]).map(f=>f.id),l=1;for(;a.includes(`c${l}`);)l++;let c={id:e?.id??`c${l}`,label:r.value.trim()||(e?.id??`c${l}`),expr:n,enabled:e?.enabled??!0};return i.apply(f=>{f.computed??=[],t!==void 0?f.computed[t]=c:f.computed.push(c)})}})}function te(i,t){let e=t!==void 0?i.doc.highlights?.[t]:void 0,r=B([{value:"row",label:"Row"},{value:"cell",label:"Cell"}],e?.scope??"row"),o=B($(i),e?.col),n=D("Highlight Column",o),a=X(i,{initial:e?.expr,placeholder:"e.g. ROUND(AMOUNT, 2) > 1000 OR NOTES IS NULL",result:"predicate"}),l=s("input",{type:"color",class:"ir-color",value:e?.style?.bg??"#fff3cd"}),c=s("input",{type:"checkbox",checked:e?!!e.style?.bg:!0}),f=s("input",{type:"color",class:"ir-color",value:e?.style?.fg??"#9f1239"}),b=s("input",{type:"checkbox",checked:!!e?.style?.fg}),d=()=>{n.hidden=r.value!=="cell"};r.onchange=d,d(),E({owner:i,title:t!==void 0?"Edit Highlight":"Highlight",width:"30rem",build:u=>u.append(D("Highlight",r),n,s("div",{class:"ir-field-label ir-condition-head"},"When"),a,s("div",{class:"ir-colors"},s("label",{class:"ir-color-pick"},c,"Background",l),s("label",{class:"ir-color-pick"},b,"Text",f))),onApply:()=>{let u=a._read();if(!c.checked&&!b.checked)throw new Error("Pick a background or text color");let p=(i.doc.highlights??[]).map(k=>k.id),v=1;for(;p.includes(`h${v}`);)v++;let m={id:e?.id??`h${v}`,enabled:e?.enabled??!0,scope:r.value,expr:u};return r.value==="cell"&&(m.col=o.value),m.style={},c.checked&&(m.style.bg=l.value),b.checked&&(m.style.fg=f.value),i.apply(k=>{k.highlights??=[],t!==void 0?k.highlights[t]=m:k.highlights.push(m)})}})}function K(i,t,{addLabel:e,max:r}){let o=s("div",{}),n=M(o,(t??[]).map(a=>({col:a})),(a,l)=>{let c=B($(i,{none:"\u2014 Select \u2014"}),l?.col??"");a.append(c),a._read=()=>c.value||null},{addLabel:e,max:r});return{container:o,list:n}}function xe(i,t){let e=s("div",{}),r=M(e,t??[],(o,n)=>{let a=B($(i,{none:"\u2014 Select \u2014"}),n?.col??""),l=s("select",{class:"ir-select"}),c=f=>{let b=a.value?i.fnsFor(i.typeOf(a.value)):[];l.replaceChildren(...b.map(d=>new Option(T[d]??d,d))),f&&b.includes(f)&&(l.value=f)};a.onchange=()=>c(l.value),c(n?.fn),o.append(l,s("span",{class:"ir-row-of"},"of"),a),o._read=()=>a.value&&l.value?{col:a.value,fn:l.value}:null},{addLabel:"Value"});return{container:e,list:r}}function H(i){let t=i.doc.view?.mode==="groupBy"?i.doc.view:i.viewMemory.groupBy,e=K(i,t?.groupBy,{addLabel:"Group Column",max:3}),r=xe(i,t?.values);E({owner:i,title:"Group By",width:"30rem",build:o=>o.append(s("div",{class:"ir-field-label"},"Group by"),e.container,e.list.addButton,s("div",{class:"ir-field-label ir-gap-above"},"Aggregate values"),r.container,r.list.addButton,s("p",{class:"ir-dialog-note"},"A row count per group is always included.")),onApply:()=>{let o=[...new Set(e.list.read())];if(!o.length)throw new Error("Pick at least one group column");let n={mode:"groupBy",groupBy:o,values:r.list.read()};return i.apply(a=>{a.view=n}).then(()=>{i.viewMemory.groupBy=n})}})}function W(i){let t=i.doc.view?.mode==="pivot"?i.doc.view:i.viewMemory.pivot,e=K(i,t?.rows,{addLabel:"Row Column",max:2}),r=K(i,t?.cols,{addLabel:"Column",max:2}),o=xe(i,t?.values);E({owner:i,title:"Pivot",width:"30rem",build:n=>n.append(s("div",{class:"ir-field-label"},"Rows"),e.container,e.list.addButton,s("div",{class:"ir-field-label ir-gap-above"},"Columns (become headings)"),r.container,r.list.addButton,s("div",{class:"ir-field-label ir-gap-above"},"Values"),o.container,o.list.addButton,s("p",{class:"ir-dialog-note"},"No values = a count per cell.")),onApply:()=>{let n=[...new Set(e.list.read())],a=[...new Set(r.list.read())].filter(c=>!n.includes(c));if(!n.length||!a.length)throw new Error("Pick at least one row column and one distinct column heading");let l={mode:"pivot",rows:n,cols:a,values:o.list.read()};return i.apply(c=>{c.view=l}).then(()=>{i.viewMemory.pivot=l})}})}function re(i,{asNew:t}){let e=!t&&i.canManageCurrentSaved(),r=s("input",{class:"ir-input",type:"text",maxLength:200,value:e?i.currentSaved.title:"",placeholder:"Saved report name"}),o=s("input",{type:"checkbox",checked:e?!!i.currentSaved.isGlobal:!1});E({owner:i,title:e?"Save Report":"Save Report As",width:"26rem",applyLabel:"Save",build:n=>{n.append(D("Name",r)),i.whoami?.isAdministrator&&n.append(s("label",{class:"ir-checkline"},o,"Global \u2014 visible to everyone with access to this report"))},onApply:()=>{let n=r.value.trim();if(!n)throw new Error("Enter a name");return i.saveReport({title:n,isGlobal:i.whoami?.isAdministrator?o.checked:!1,asNew:!e})}})}var Re=new URL("..",import.meta.url).pathname.replace(/\/$/,""),ye=(i,t)=>typeof i=="string"&&typeof t=="string"&&i.toUpperCase()===t.toUpperCase(),ie=class extends HTMLElement{static observedAttributes=["report","api-base","base"];constructor(){super();let{root:t,mount:e}=le(this);this._root=t,this._mount=e,this._seq=0,this._initialized=!1}get apiBase(){return this.getAttribute("api-base")??this.getAttribute("base")??Re}set apiBase(t){t==null?this.removeAttribute("api-base"):this.setAttribute("api-base",String(t))}get base(){return this.apiBase.replace(/\/+$/,"")}get requestedReportName(){return this.getAttribute("report")}get reportName(){return this._activeReportName??this.requestedReportName}connectedCallback(){this.scheduleInit()}disconnectedCallback(){++this._seq,this._abort?.abort(),this._abort=null,ce(this)}attributeChangedCallback(t,e,r){this._initialized&&e!==r&&this.scheduleInit()}scheduleInit(){this._initQueued||(this._initQueued=!0,queueMicrotask(()=>{this._initQueued=!1,this.isConnected&&this.init()}))}async init(){let t=++this._seq;this._initialized=!0,this._abort?.abort(),this._abort=null,this.schema=null,this.doc=null,this.lastResult=null,this.availableReports=[],this._activeReportName=null,this.whoami=null,this.savedList=[],this.currentSaved=null,this.searchScopeCol=null,this.viewMemory={},this.buildSkeleton();try{let[e,r]=await Promise.all([R(this.base),R(`${this.base}/whoami`).catch(()=>null)]);if(t!==this._seq)return;this.availableReports=e,this.whoami=r,this.refreshReportSelect();let o=this.requestedReportName,n=this.availableReports.find(l=>ye(l.name,o)),a=n?[n,...this.availableReports.filter(l=>l!==n)]:this.availableReports;if(!a.length){this.showError(new Error("No reports are available for the current user."));return}for(let l of a)if(await this.activateReport(l.name,t,{quiet:!0})||t!==this._seq)return;this.showError(new Error("None of the reports available to the current user could be loaded."))}catch(e){e.name!=="AbortError"&&t===this._seq&&this.showError(e)}}refreshReportSelect(){let{reportSel:t,reportWrap:e}=this.els;t.replaceChildren(...this.availableReports.map(r=>new Option(r.title,r.name))),t.value=this._activeReportName??"",e.hidden=this.availableReports.length<=1}async activateReport(t,e=++this._seq,{quiet:r=!1}={}){let o=this.availableReports.find(n=>ye(n.name,t));if(!o||e!==this._seq)return!1;this._abort?.abort(),this._abort=null,this._activeReportName=o.name,this.schema=null,this.doc=null,this.lastResult=null,this.savedList=[],this.currentSaved=null,this.searchScopeCol=null,this.viewMemory={},this.els.search.value="",this.els.table.replaceChildren(),this.els.pager.replaceChildren(),this.els.chips.replaceChildren(),this.els.chips.hidden=!0,this.clearError(),this.refreshReportSelect(),this.refreshSavedSelect(),this._mount.classList.add("ir-busy");try{let n=await R(`${this.base}/${encodeURIComponent(o.name)}/schema`);if(e!==this._seq)return!1;let a=await R(`${this.base}/${encodeURIComponent(o.name)}/saved`).catch(()=>[]);return e!==this._seq?void 0:(this.schema=n,this.savedList=a,this.doc=this.normalize(n.defaultState),this.els.search.value=this.doc.search??"",this.refreshSavedSelect(),await this.runQuery({quiet:r}),e===this._seq&&this.lastResult!==null)}catch(n){return!r&&n.name!=="AbortError"&&e===this._seq&&this.showError(n),!1}finally{e===this._seq&&this._mount.classList.remove("ir-busy")}}buildSkeleton(){let t=s("button",{type:"button",class:"ir-btn ir-search-scope",title:"Choose search column","aria-label":"Choose search column",onclick:()=>this.openSearchScopeMenu(t)},O("search"),O("caret")),e=s("input",{class:"ir-search-input",type:"search",placeholder:"Search",onkeydown:d=>{d.key==="Enter"&&this.doSearch()}}),r=s("button",{type:"button",class:"ir-btn ir-go",onclick:()=>this.doSearch()},"Go"),o=(d,u,p)=>s("button",{type:"button",class:"ir-btn ir-viewbtn",dataset:{mode:d},title:p,"aria-label":p,onclick:()=>this.switchView(d)},O(u)),n=s("div",{class:"ir-viewbtns",role:"group","aria-label":"View"},o("grid","grid","Grid"),o("groupBy","group","Group By"),o("pivot","pivot","Pivot")),a=s("button",{type:"button",class:"ir-btn ir-actionsbtn",onclick:()=>this.openActionsMenu(a)},"Actions",O("caret")),l=s("select",{class:"ir-select ir-saved-select",onchange:()=>l.value?this.loadSavedById(l.value):this.resetToPrimary()}),c=s("label",{class:"ir-saved",hidden:!0},s("span",{class:"ir-saved-label"},"Saved Report"),l),f=s("select",{class:"ir-select ir-report-select",part:"report-select",onchange:()=>this.activateReport(f.value)}),b=s("label",{class:"ir-saved",hidden:!0},s("span",{class:"ir-saved-label"},"Report"),f);this.els={search:e,views:n,reportSel:f,reportWrap:b,savedSel:l,savedWrap:c,errorSlot:s("div",{}),transientSlot:s("div",{}),ignoredSlot:s("div",{}),chips:s("div",{class:"ir-chips",part:"chips",hidden:!0}),table:s("table",{class:"ir-table",part:"table"}),pager:s("div",{class:"ir-pager",part:"pager"})},this._mount.replaceChildren(s("div",{class:"ir-toolbar",part:"toolbar"},s("div",{class:"ir-search"},t,e,r),n,a,s("span",{class:"ir-spacer"}),b,c),s("div",{class:"ir-busybar"}),s("div",{class:"ir-notices",part:"notices"},this.els.errorSlot,this.els.transientSlot,this.els.ignoredSlot),this.els.chips,s("div",{class:"ir-tablewrap",part:"table-container"},this.els.table),this.els.pager)}pickable(){return this.lastResult?.availableColumns??this.schema?.columns??[]}typeOf(t){return this.pickable().find(e=>e.name===t)?.type??"other"}labelOf(t){return this.pickable().find(e=>e.name===t)?.label??t}fnsFor(t){let e=this.schema?.capabilities?.aggregateFunctions??{};return e[t]??e.other??[]}expressionFunctions(){return this.schema?.capabilities?.expressionFunctions??[]}visibleColumnNames(){return this.doc?.columns?.length?[...this.doc.columns]:this.pickable().map(t=>t.name)}normalize(t){return fe(t,this.schema?.limits?.defaultPageSize??50,this.schema?.defaultState)}serialize(){return ge(this.doc,this.schema?.stateVersion??2)}async runQuery(t={}){this._abort?.abort();let e=this._abort=new AbortController;this._mount.classList.add("ir-busy");try{let r=await R(`${this.base}/${encodeURIComponent(this.reportName)}/query`,{method:"POST",body:this.serialize(),signal:e.signal});return e!==this._abort?void 0:(this.lastResult=r,this.clearError(),this.doc.view?.mode&&this.doc.view.mode!=="grid"&&(this.viewMemory[this.doc.view.mode]=this.doc.view),q(this,this.els.chips),pe(this,this.els.table),ue(this,this.els.pager),this.renderIgnored(r.ignored),this.refreshViewButtons(),r)}catch(r){if(r.name==="AbortError")return;throw q(this,this.els.chips),t.quiet||this.showError(r),r}finally{e===this._abort&&this._mount.classList.remove("ir-busy")}}async apply(t,{resetPage:e=!0}={}){let r=structuredClone(this.doc);t(this.doc),e&&this.doc.page&&(this.doc.page.index=1);try{await this.runQuery({quiet:!0})}catch(o){throw this.doc=r,q(this,this.els.chips),o}}applyOrBanner(t,e){return this.apply(t,e).catch(r=>this.showError(r))}showError(t){let e=t?.status===401?"Sign in to use this report.":t?.status===404?"Report not found \u2014 or you don't have access.":t?.message||String(t),r=t?.traceId?` (ref ${t.traceId})`:"";this.els.errorSlot.replaceChildren(j("error",e+r,()=>this.clearError()))}clearError(){this.els.errorSlot.replaceChildren()}notify(t,e="ok"){let r=j(e,t);this.els.transientSlot.append(r),setTimeout(()=>r.remove(),4e3)}renderIgnored(t){if(!t?.length){this.els.ignoredSlot.replaceChildren();return}let e="Some settings were ignored: "+t.map(r=>`${r.kind} (${r.detail})`).join("; ");this.els.ignoredSlot.replaceChildren(j("warn",e,()=>this.els.ignoredSlot.replaceChildren()))}doSearch(){let t=this.els.search.value.trim();if(!this.searchScopeCol){this.applyOrBanner(n=>{n.search=t});return}if(!t)return;let e=this.searchScopeCol,r=this.typeOf(e),o;try{o=be(e,r,t)}catch(n){this.showError(n);return}this.els.search.value="",this.applyOrBanner(n=>{(n.filters??=[]).push({enabled:!0,expr:o})})}openSearchScopeMenu(t){let e=this.pickable().filter(r=>["text","number","date","bool"].includes(r.type));_(t,[{label:"All Text Columns",checked:!this.searchScopeCol,onPick:()=>this.setSearchScope(null)},"-",...e.map(r=>({label:r.label,checked:this.searchScopeCol===r.name,onPick:()=>this.setSearchScope(r.name)}))])}setSearchScope(t){this.searchScopeCol=t,this.els.search.placeholder=t?`Search: ${this.labelOf(t)}`:"Search",this.els.search.focus()}refreshViewButtons(){let t=this.doc?.view?.mode??"grid";for(let e of this.els.views.children)e.classList.toggle("ir-active",e.dataset.mode===t)}switchView(t){let e=this.doc.view?.mode??"grid";if(t===e)return;if(t==="grid"){this.applyOrBanner(o=>{o.view={mode:"grid"}});return}let r=this.viewMemory[t];r?this.applyOrBanner(o=>{o.view=r}):t==="groupBy"?H(this):W(this)}openActionsMenu(t){let e=this.canManageCurrentSaved();_(t,[{label:"Columns\u2026",onPick:()=>me(this)},{label:"Filter\u2026",onPick:()=>F(this,{})},{label:"Sort\u2026",onPick:()=>ve(this)},"-",{label:"Control Break\u2026",onPick:()=>J(this)},{label:"Highlight\u2026",onPick:()=>te(this)},{label:"Aggregate\u2026",onPick:()=>Z(this)},{label:"Compute\u2026",onPick:()=>ee(this)},"-",{label:"Group By\u2026",onPick:()=>H(this)},{label:"Pivot\u2026",onPick:()=>W(this)},{heading:"Report"},...e?[{label:"Save",onPick:()=>re(this,{asNew:!1})}]:[],{label:"Save As\u2026",onPick:()=>re(this,{asNew:!0})},...e?[{label:"Delete\u2026",onPick:()=>this.deleteCurrentSaved()}]:[],{label:"Reset",onPick:()=>this.resetWorkingCopy()},{heading:"Download"},{label:"CSV",onPick:()=>this.exportCsv()}])}openHeaderMenu(t,e){let r=this.doc.view?.mode??"grid",o=[{label:"Sort Ascending",onPick:()=>this.applyOrBanner(l=>{l.sorts=[{col:t,dir:"asc"}]})},{label:"Sort Descending",onPick:()=>this.applyOrBanner(l=>{l.sorts=[{col:t,dir:"desc"}]})}];if(r!=="grid"){_(e,o);return}let n=this.visibleColumnNames(),a=(this.doc.breaks??[]).includes(t);_(e,[...o,"-",{label:"Hide Column",disabled:n.length<=1,onPick:()=>this.applyOrBanner(l=>{l.columns=n.filter(c=>c!==t)})},{label:a?"Remove Control Break":"Control Break",checked:a,onPick:()=>this.applyOrBanner(l=>{l.breaks=a?(l.breaks??[]).filter(c=>c!==t):[...l.breaks??[],t]})},"-",{label:"Filter\u2026",onPick:()=>F(this,{col:t})}])}chipArray(t,e){return{filter:t.filters,aggregate:t.aggregates,computed:t.computed,highlight:t.highlights}[e]}chipToggle(t,e,r){this.applyOrBanner(o=>{if(t!=="filter"&&t!=="computed"&&t!=="highlight")return;let n=this.chipArray(o,t)?.[e];n&&(n.enabled=r)})}chipRemove(t,e){this.applyOrBanner(r=>{switch(t){case"search":r.search="",this.els.search.value="";break;case"break":{r.breaks=(r.breaks??[]).filter((o,n)=>n!==e);break}case"view":r.view={mode:"grid"};break;default:this.chipArray(r,t)?.splice(e,1)}})}chipEdit(t,e){switch(t){case"search":this.els.search.focus(),this.els.search.select();break;case"filter":F(this,{editIndex:e});break;case"break":J(this);break;case"aggregate":Z(this);break;case"computed":ee(this,e);break;case"highlight":te(this,e);break;case"view":this.doc.view?.mode==="pivot"?W(this):H(this);break}}gotoPage(t){this.applyOrBanner(e=>{e.page.index=t},{resetPage:!1})}setPageSize(t){this.applyOrBanner(e=>{e.page.size=t})}canManageCurrentSaved(){let t=this.currentSaved;return t?this.whoami?.isAdministrator||t.mine&&!t.isGlobal:!1}refreshSavedSelect(){let{savedSel:t,savedWrap:e}=this.els;t.replaceChildren(new Option("Primary Report",""));let r=(o,n)=>{if(!n.length)return;let a=s("optgroup",{label:o});for(let l of n)a.append(new Option(l.title+(l.mine||l.isGlobal?"":` (${l.owner})`),l.id));t.append(a)};r("Global",this.savedList.filter(o=>o.isGlobal)),r("Private",this.savedList.filter(o=>!o.isGlobal)),t.value=this.currentSaved?.id??"",e.hidden=this.savedList.length===0}async loadSavedList(){this.savedList=await R(`${this.base}/${encodeURIComponent(this.reportName)}/saved`).catch(()=>[])}async loadSavedById(t){try{let e=await R(`${this.base}/saved/${encodeURIComponent(t)}`);this.currentSaved=e.summary,this.doc=this.normalize(e.state),this.els.search.value=this.doc.search??"",this.refreshSavedSelect(),await this.runQuery()}catch(e){e.name!=="AbortError"&&this.showError(e)}}resetToPrimary(){this.currentSaved=null,this.doc=this.normalize(this.schema?.defaultState),this.els.search.value=this.doc.search??"",this.refreshSavedSelect(),this.runQuery().catch(()=>{})}async resetWorkingCopy(){let t=this.currentSaved?`"${this.currentSaved.title}"`:"its default settings";await Y(this,"Reset",`Restore this report to ${t}? Unsaved changes are lost.`,"Reset")&&(this.currentSaved?await this.loadSavedById(this.currentSaved.id):this.resetToPrimary())}async saveReport({title:t,isGlobal:e,asNew:r}){let o=this.serialize();if(r)this.currentSaved=await R(`${this.base}/${encodeURIComponent(this.reportName)}/saved`,{method:"POST",body:{title:t,state:o,isGlobal:e}});else{let n={title:t,state:o};this.whoami?.isAdministrator&&(n.isGlobal=e),this.currentSaved=await R(`${this.base}/saved/${encodeURIComponent(this.currentSaved.id)}`,{method:"PUT",body:n})}await this.loadSavedList(),this.refreshSavedSelect(),this.notify("Report saved.")}async deleteCurrentSaved(){let t=this.currentSaved;if(t&&await Y(this,"Delete Saved Report",`Delete "${t.title}"? This cannot be undone.`))try{await R(`${this.base}/saved/${encodeURIComponent(t.id)}`,{method:"DELETE"}),this.currentSaved=null,await this.loadSavedList(),this.resetToPrimary(),this.notify("Saved report deleted.")}catch(e){this.showError(e)}}async exportCsv(){try{let{blob:t,filename:e,truncated:r}=await ne(`${this.base}/${encodeURIComponent(this.reportName)}/export?format=csv`,this.serialize());se(t,e??`${this.reportName}.csv`),r&&this.notify("Export truncated at the report's row cap.","warn")}catch(t){this.showError(t)}}};customElements.get("interactive-report")||customElements.define("interactive-report",ie);
//# sourceMappingURL=ir.js.map
