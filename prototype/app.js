const $=s=>document.querySelector(s);
const $$=s=>Array.from(document.querySelectorAll(s));
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function icon(n){return `<svg viewBox="0 0 18 18" aria-hidden="true"><path d="${paths[n]||paths.file}"/></svg>`}
function hydrate(){$$('[data-i]').forEach(e=>e.innerHTML=icon(e.dataset.i))}
let prefs={pet:true,motion:matchMedia('(prefers-reduced-motion:reduce)').matches,density:'comfortable',font:14,left:244,right:300,collapsed:false};
try{Object.assign(prefs,JSON.parse(localStorage.getItem('dougong-prototype-layout')||'{}'))}catch{}
let providerCounter=2,sessionCounter=3,pluginCounter=1,view='conversation',dock='files',settingsTab='providers',pluginTab='agent',searchPlugins='',selectedProvider='custom',filePreview='',statusOpen=false,contextOpen=false,dialogReturn=null,modalConfirm=null,petRestore='idle';
const providers=[{id:'custom',name:'本地开发配置',protocol:'OpenAI Chat Completions',url:'https://api.example.com/v1',model:'example-model',context:128000,output:8192,credential:'system'},{id:'backup',name:'备用演示配置',protocol:'Anthropic Messages',url:'https://api.example.com',model:'example-backup',context:32000,output:4096,credential:'environment'}];
let defaultProvider='custom';
const sessions=[
 {id:'structure',title:'梳理项目结构',workspace:'desktop',state:'pending',elapsed:12.4,provider:'custom',messages:[],task:'帮我梳理这个项目的结构，重点看看会话是如何装配的。\n先阅读相关文件，不要修改代码。',notice:'',decision:'',started:true},
 {id:'contract',title:'检查 IPC 消息契约',workspace:'desktop',state:'completed',elapsed:8.7,provider:'custom',messages:[],task:'整理 IPC 层的消息类型，并说明它和界面的关系。',notice:'',decision:'once',started:true},
 {id:'sdk',title:'阅读 SDK 接入说明',workspace:'sdk',state:'idle',elapsed:0,provider:'backup',messages:[],task:'',notice:'',decision:'',started:false}
];
let active='structure';
sessions.forEach(s=>s.config={...provider(s.provider)});
const plugins=[{id:'sample',name:'示例工具插件',description:'演示 Agent 插件的装载、启用与卸载。',identity:'Dougong.Sample.Plugin',type:'agent',enabled:true,builtin:false,error:'',author:'示例作者',elapsed:'12 ms（模拟）'},
 {id:'files',name:'工作区文件工具',description:'读取文件、列出目录，受当前工作区边界约束。',identity:'builtin.file-tools',type:'agent',enabled:true,builtin:true,error:'',author:'内置',elapsed:'—'},
 {id:'shell',name:'清晰工作台',description:'侧栏、会话和右侧工具面板的内置界面。',identity:'builtin.ui.shell',type:'ui',enabled:true,builtin:true,error:'',author:'内置',elapsed:'—'},
 {id:'pet',name:'榫灵陪伴',description:'嵌在窗口内的安静陪伴，可在设置中隐藏。',identity:'builtin.ui.sunling',type:'ui',enabled:prefs.pet,builtin:true,error:'',author:'内置',elapsed:'—'}];
const files={
 'SessionComposer.cs':['namespace DougongDesktop.Framework;','','// 说明性代码预览，未读取真实文件','public sealed class SessionComposer','{','    // 配置模型、会话存储与运行时','    // 审批请求通过 IPC 交给界面','}'],
 'SessionRuntime.cs':['namespace DougongDesktop.Framework;','','// 原型中的结构说明','// 投影会话快照及增量事件','// 运行状态由引擎推送，不由 UI 猜测'],
 'IpcApprover.cs':['namespace DougongDesktop.Framework;','','// 原型中的结构说明','// 请求绑定会话、回合、参数摘要与工作区','// 每个审批只能答复一次'],
 'Tools.cs':['namespace DougongDesktop.Framework;','','// 原型中的工具说明','// read_file：只读工作区文件','// list_directory：列出工作区目录','// 路径不能越过工作区边界'],
 'Protocol.cs':['namespace DougongDesktop.Ipc;','','// 原型中的契约说明','// Thread / Turn / Item','// ConversationItem 按 kind 选择视图','// 未知条目降级展示，不丢弃'],
 'Program.cs':['// 原型中的引擎入口说明','// AgentHost 承载 SDK 运行时','// UI 通过本地 IPC 请求会话状态']
};
function current(){return sessions.find(s=>s.id===active)}
function provider(id){return providers.find(p=>p.id===id)||providers[0]}
function busy(s=current()){return ['pending','thinking','executing','disconnected'].includes(s.state)}
function phase(s=current()){return {idle:'空闲',pending:'等待审批',thinking:'思考中',executing:'执行中',completed:'已完成',denied:'等待输入',stopped:'已停止',expired:'审批已过期',unknown:'结果未知',failed:'工具失败',disconnected:'引擎未连接'}[s.state]||'空闲'}
function toast(text){$('#toast').textContent=text;$('#toast').classList.remove('hidden');clearTimeout(window.toastTimer);window.toastTimer=setTimeout(()=>$('#toast').classList.add('hidden'),3000)}
function savePrefs(){try{localStorage.setItem('dougong-prototype-layout',JSON.stringify(prefs))}catch{} applyPrefs()}
function applyPrefs(){
 document.documentElement.style.setProperty('--sidebar-width',Math.max(200,Math.min(320,prefs.left))+'px');
 document.documentElement.style.setProperty('--inspector-width',Math.max(250,Math.min(420,prefs.right))+'px');
 document.documentElement.style.setProperty('--reading-size',prefs.font+'px');
 document.body.classList.toggle('density-compact',prefs.density==='compact');document.body.classList.toggle('reduce-motion',prefs.motion);
 $('#app').classList.toggle('collapsed',prefs.collapsed);$('#pet-place').classList.toggle('hidden',!prefs.pet);$('#collapse-sidebar').setAttribute('aria-label',prefs.collapsed?'展开侧栏':'收起侧栏');
}
function renderSidebar(){
 const scroll=$('.workspace-scroll');
 scroll.innerHTML=`<div class="section-title">工作区<button id="workspace-info" aria-label="工作区说明" title="工作区说明">${icon('info')}</button></div>`+
 [['desktop','Dougong Desktop'],['sdk','Dougong SDK']].map(([id,name])=>`<button class="workspace-name" data-workspace="${id}" aria-expanded="true">${icon('chevron')}${icon('folder')}${name}</button><div class="history" data-workspace-list="${id}">${sessions.filter(s=>s.workspace===id).map(s=>`<button class="session ${active===s.id&&view==='conversation'?'selected':''}" data-session="${s.id}" title="${esc(s.title)}"><span>${esc(s.title)}</span>${s.id===active?'<small>当前</small>':''}</button>`).join('')}</div>`).join('');
 $$('[data-view]').forEach(b=>b.classList.toggle('active',b.dataset.view===view));
}
function petState(state){if(!$('#pet-place img'))return;$('#pet-place img').src=petAssets[state]||petAssets.idle;petRestore=state}
function updateStatus(){
 const s=current();$('#state-label').textContent=phase(s);$('#elapsed').textContent=s.started?s.elapsed.toFixed(1)+' 秒':'—';
 const muted=['pending','expired','unknown'].includes(s.state),good=s.state==='completed';
 $('.capsule .state').style.color=muted?'#805c26':good?'#276a53':'#526172';$('.capsule .dot').style.background=muted?'#a17d3e':good?'#43866a':'#6e7a88';
 $('#status-capsule').setAttribute('aria-label',phase(s)+'，'+$('#elapsed').textContent+'，展开步骤');$('#status-capsule').setAttribute('aria-expanded',String(statusOpen));
 $('#steps').classList.toggle('hidden',!statusOpen);$('#steps').innerHTML=s.started?`✓ 浏览工作区文件<br>✓ 确认工程分层<br>${s.state==='completed'?'✓ 已完成模拟读取与整理':s.state==='pending'?'◷ 等待授权，计时已暂停':esc(phase(s))}`:'还没有开始执行步骤。';
 const engine=$('.engine');engine.textContent=s.state==='disconnected'?'引擎连接中断':busy(s)&&s.state!=='pending'?'本地引擎工作中':'本地引擎（演示）';engine.classList.toggle('failed',s.state==='disconnected');engine.classList.toggle('busy',['thinking','executing'].includes(s.state));
 const status=s.state==='thinking'?'thinking':s.state==='executing'?'working':'idle';petState(status);
 const petText=$('#pet-place div');if(petText)petText.innerHTML='<strong>榫灵在这里</strong>'+(['thinking','executing'].includes(s.state)?'我在认真整理。':s.state==='pending'?'等你确认后，再继续。':'慢慢来，我陪你。');
}
function renderView(v=view){
 contextOpen=false;view=v;const s=current();$('#page-title').textContent=v==='plugins'?'插件管理':v==='settings'?'设置':v==='onboarding'?'欢迎使用 Dougong':v==='recovery'?'恢复工作现场':s.title;
 $('.crumb').textContent=(s.workspace==='desktop'?'Dougong Desktop':'Dougong SDK')+' / '+({plugins:'已安装的插件',settings:'工作台偏好',onboarding:'首次配置',recovery:'会话恢复'}[v]||'当前会话');
 $('#content').innerHTML=v==='plugins'?renderPlugins():v==='settings'?renderSettings():v==='onboarding'?renderOnboarding():v==='recovery'?renderRecovery():renderConversation();
 renderSidebar();renderDock();updateStatus();applyPrefs();hydrate();
}
function toolSummary(){return `<details class="tools"><summary><span class="check">${icon('check')}</span><span>已浏览项目目录</span><span class="tooltime">list_directory，0.2 秒</span>${icon('chevron')}</summary><div class="tool-detail"><pre>src/\n├ DougongDesktop.Ipc\n├ DougongDesktop.Framework\n└ DougongDesktop.AgentHost</pre><p class="field-help">演示输出；完整结果可展开查看。</p></div></details>`}
function approvalCard(s){
 if(s.state==='pending')return `<div class="approval" id="approval-card"><div class="approval-top"><span class="approval-icon">${icon('shield')}</span><strong>允许读取这个文件吗？</strong><span class="badge">需要审批</span></div><p>此操作只会读取文件内容，不会修改工作区。</p><div class="path-box"><span class="label">read_file</span><code>src/DougongDesktop.Framework/SessionComposer.cs</code></div><div class="scope">${icon('folder')}<span>范围：${s.workspace==='desktop'?'Dougong Desktop':'Dougong SDK'} 工作区</span></div><div class="approval-actions"><button class="primary" data-decision="once">允许一次</button><button data-decision="session">本会话允许</button><button class="danger" data-decision="deny">拒绝</button><button class="danger" data-decision="stop">拒绝并停止</button></div></div><div class="approval-foot">${icon('lock')}授权绑定本次会话、文件参数与工作区；参数变化需重新确认。</div>`;
 if(['thinking','executing'].includes(s.state))return `<div class="tools"><div class="approval-top"><span class="approval-icon">${icon('code')}</span><strong>${s.state==='thinking'?'正在准备读取步骤':'正在读取会话装配代码'}</strong><span class="badge busy">模拟执行</span></div><div class="path-box"><code>src/DougongDesktop.Framework/SessionComposer.cs</code></div><p style="padding:12px 18px;margin:0;color:#536171">${s.state==='thinking'?'还没有调用工具。':'已授权，仅执行当前匹配的读取请求。'}</p></div>`;
 const outcomes={completed:['check',s.decision==='session'?'本会话已允许此读取':'已允许一次读取','good','当前请求已处理，重复操作不会产生第二次授权。'],denied:['close','已拒绝此读取','neutral','文件没有被读取；你可以调整范围后继续。'],stopped:['close','已拒绝并停止回合','neutral','本回合已终止，没有执行文件读取。'],expired:['lock','审批已过期','neutral','这份授权请求已失效；旧按钮不能继续使用。'],unknown:['info','结果未知，请人工核实，不要重试','error','连接在工具返回前中断，不能确定是否完成。请核实结果后再决定后续操作。'],failed:['info','文件读取失败','error','模拟原因：目标文件不存在。工作区没有发生修改。'],disconnected:['info','引擎连接中断','error','会话界面已保留。恢复连接会先同步状态，不会自动重放工具。']};
 const [ic,title,tone,body]=outcomes[s.state]||outcomes.completed;
 return `<div class="approval done"><div class="approval-top"><span class="approval-icon">${icon(ic)}</span><strong>${title}</strong><span class="badge ${tone}">${s.state==='unknown'?'待核实':s.state==='failed'?'失败':s.state==='expired'?'已失效':'已处理'}</span></div><p>${body}</p>${s.state==='disconnected'?'<div class="approval-actions"><button data-action="reconnect" class="primary">重新连接引擎</button></div>':''}</div>`;
}
function followup(s){
 if(s.state!=='completed')return '';
 return `<details class="tools"><summary><span class="check">${icon('check')}</span><span>已读取 SessionComposer.cs</span><span class="tooltime">执行 0.7 秒（模拟）</span>${icon('chevron')}</summary><div class="tool-detail"><p>审批等待与工具执行分别计时。下方是说明性结果，并非实时源码。</p><pre>Framework → 配置模型与会话存储\nFramework → 装配运行时与审批器\nAgentHost → 承载引擎入口\nUI → 通过 IPC 接收状态投影</pre></div></details><p>项目按职责分为三层：</p><ul><li><strong>Ipc</strong> 定义消息与本地传输契约。</li><li><strong>Framework</strong> 连接 SDK，装配会话与审批。</li><li><strong>AgentHost</strong> 承载引擎进程，处理界面请求。</li></ul><p>UI 消费会话投影；核心能力通过框架层接入。这个任务没有修改代码，可以在右栏继续查看计划与上下文。</p><div class="assistant-actions"><button data-dock="plan" class="btn">查看完成的计划</button></div>`;
}
function renderConversation(){
 const s=current(),locked=busy(s),empty=!s.task;
 const messages=s.messages.map(m=>m.role==='user'?`<div class="user" style="margin-top:22px;white-space:pre-wrap">${esc(m.text)}</div><div class="user-meta">你</div>`:`<div class="assistant-label" style="margin-top:24px"><img src="${logo}" alt=""><strong>Dougong</strong></div><p>${esc(m.text)}</p>`).join('');
 const content=empty?`<div class="recovery"><img src="${logo}" alt="Dougong" width="42"><h2>从一件具体的事开始</h2><p>工作区已就绪。描述你的目标，Dougong 会把执行步骤与工具请求放在这里。</p><div class="note">本地交互演示；不会连接模型或读取真实文件。</div><div class="form-actions"><button class="btn" data-action="starter">梳理项目结构</button><button class="btn" data-view="settings">检查模型配置</button></div></div>`:`<div class="date">今天，10月30日</div><div class="user" style="white-space:pre-wrap">${esc(s.task)}</div><div class="user-meta">你，10:24</div><div class="assistant-label"><img src="${logo}" alt=""><strong>Dougong</strong><span>本地工作区</span></div><div class="assistant-body"><p>我会先查看工程目录，再阅读会话装配代码，整理各层的职责和调用关系。</p>${toolSummary()}<p>工程按消息契约、框架适配和引擎入口分层。下一步阅读 <code>SessionComposer.cs</code>，确认会话的装配方式。</p>${approvalCard(s)}${followup(s)}</div>`;
 const notice=s.notice==='compacted'?`<div class="notice warning">${icon('layers')}<div><strong>上下文已压缩，部分细节可能丢失</strong><p>历史记录仍保留完整原文，发给模型的上下文已改为摘要。这是有损压缩；关键约束请再次确认。</p></div></div>`:s.notice==='notCompacted'?`<div class="notice warning">${icon('info')}<div><strong>上下文压缩未成功</strong><p>没有替换原有上下文。用量仍按压缩前显示。</p></div></div>`:'';
 const lock=s.state==='pending'?'请先处理上方审批，再继续对话':s.state==='disconnected'?'请先恢复引擎连接；不会自动重放工具':locked?'Dougong 正在工作，你可以停止本轮':'';
 return `<section class="conversation" id="conversation">${notice}${content}<div id="followup">${messages}</div></section><div class="composer-wrap"><div class="input-lock ${lock?'':'hidden'}" id="input-lock">${icon('lock')}${lock}</div><div class="composer"><textarea id="message" rows="2" aria-label="消息" placeholder="${s.state==='pending'?'等待你确认文件读取权限…':locked?'等待当前步骤结束…':'描述你的任务…'}" ${locked?'disabled':''}></textarea><div class="composer-bottom"><div class="composer-left"><button class="model" data-action="model" title="${esc((s.config||provider(s.provider)).name)}"><span>${esc((s.config||provider(s.provider)).model)}</span>${icon('chevron')}</button>${contextControl(s)}</div><button class="send" id="send-message" data-action="${['thinking','executing'].includes(s.state)?'cancel-turn':'send'}" ${s.state==='pending'||s.state==='disconnected'?'disabled':''} aria-label="${['thinking','executing'].includes(s.state)?'停止本轮':'发送消息'}">${icon(['thinking','executing'].includes(s.state)?'close':'send')}</button></div></div><div class="composer-hint">模拟运行，不执行真实操作。Enter 发送，Shift + Enter 换行</div></div>`;
}
function contextInfo(s=current()){
 const config=s.config||provider(s.provider),max=config.context,compacted=s.notice==='compacted',percent=s.started?(compacted?11:24):0;
 const used=Math.round(max*percent/100),display=n=>n>=1000?(n/1000).toFixed(1).replace(/\.0$/,'')+'k':String(n);
 return {max,used,percent,compacted,output:config.output,display};
}
function contextControl(s){
 const info=contextInfo(s),fmt=info.display;
 return `<div class="context-area"><button id="context-trigger" data-action="context" class="context-trigger" aria-label="上下文用量，已使用 ${info.percent}%" aria-expanded="${contextOpen}" aria-controls="context-popover" title="上下文 ${info.percent}%，点击查看详情"><svg viewBox="0 0 20 20" class="context-ring" aria-hidden="true"><circle cx="10" cy="10" r="7" class="ring-track"></circle><circle cx="10" cy="10" r="7" class="ring-value" stroke-dasharray="${44*info.percent/100} 44"></circle></svg><span>${info.percent}%</span></button><section id="context-popover" class="context-popover ${contextOpen?'':'hidden'}" aria-label="上下文用量详情"><div class="context-popover-head"><strong>上下文用量</strong><button class="iconbtn" data-action="close-context" aria-label="收起上下文详情">${icon('close')}</button></div><p class="context-popover-summary"><strong>${info.percent}%</strong> 已使用 <span>${fmt(info.used)} / ${fmt(info.max)} tokens</span></p><div class="context-segments"><i style="width:${info.percent*.4}%"></i><i style="width:${info.percent*.3}%"></i><i style="width:${info.percent*.2}%"></i><i style="width:${info.percent*.1}%"></i></div><div class="context-legend"><div><span>系统与工具</span><span>${fmt(Math.round(info.used*.3))}</span></div><div><span>会话消息</span><span>${fmt(Math.round(info.used*.4))}</span></div><div><span>工具结果</span><span>${fmt(info.used-Math.round(info.used*.3)-Math.round(info.used*.4))}</span></div><div><span>最大输出</span><span>${info.output.toLocaleString()} tokens</span></div><div><span>压缩状态</span><span>${info.compacted?'已应用摘要':'尚未压缩'}</span></div></div><p class="context-footnote">模拟用量。压缩影响发给模型的上下文，完整会话记录仍保留。</p></section></div>`;
}
function decide(value){
 const s=current();if(s.state!=='pending')return;
 s.decision=value;
 if(value==='deny'||value==='stop'){s.state=value==='deny'?'denied':'stopped';renderView();return}
 s.state='executing';renderView();
 setTimeout(()=>{if(s.state!=='executing')return;s.state='completed';s.elapsed=13.1;if(current()===s){renderView();petState('success');setTimeout(()=>{if(current()===s&&s.state==='completed')petState('idle')},1200)}},950);
}
function newSession(){
 if(busy()){toast('请先完成审批或停止当前回合，再新建会话。');return}
 const id='session-'+(++sessionCounter);sessions.push({id,title:'新建会话',workspace:current().workspace,state:'idle',elapsed:0,provider:defaultProvider,config:{...provider(defaultProvider)},messages:[],task:'',notice:'',decision:'',started:false});active=id;renderView('conversation');$('#message').focus();
}
function sendMessage(text){
 const s=current(),box=$('#message');text=(text??box?.value??'').trim();if(!text||busy(s))return;
 if(s.state==='unknown'){toast('请先人工核实未知结果。可以新建会话继续其他任务，不会重放这次工具。');return}
 if(!s.task){s.task=text;s.title=text.length>14?text.slice(0,14)+'…':text;s.state='thinking';s.started=true;s.elapsed=0;renderView();setTimeout(()=>{if(s.state==='thinking'){s.state='pending';if(current()===s)renderView()}},900);return}
 s.messages.push({role:'user',text},{role:'assistant',text:'已将这条消息加入原型会话。正式应用会交由 AgentHost 处理；此处不执行模型请求或工具调用。'});renderView();$('#conversation').scrollTop=$('#conversation').scrollHeight;
}
function planHtml(){const s=current(),done=s.state==='completed',started=s.started;return `<div class="subtitle"><span>${esc(s.title)}</span><span>演示计划</span></div>`+['浏览工作区文件','确认工程分层','阅读会话装配代码','整理调用关系'].map((name,i)=>{const completed=started&&(i<2||done),cur=started&&i===2&&!done;return `<div class="planstep"><span class="stepmark ${completed?'done':cur?'current':''}">${completed?'✓':cur?'◷':'○'}</span><div>${name}<small>${completed?'已完成':cur?esc(phase(s)):'待开始'}</small></div></div>`}).join('')+`<div class="note">${!started?'执行开始后，计划会随明确的计划事件更新。':done?'本轮计划已完成，工作区没有改动。':'未完成的步骤保持未完成；拒绝或工具错误不视作成功。'}</div>`}
function renderDock(){
 const titles={files:['folder','工作区文件'],changes:['diff','本回合改动'],plan:['list','执行计划'],terminal:['terminal','终端']};
 $('#inspector-title').textContent=titles[dock][1];$('#inspector-icon').dataset.i=titles[dock][0];$('#inspector-icon').innerHTML=icon(titles[dock][0]);$$('nav.dock [data-dock]').forEach(b=>{b.classList.toggle('active',b.dataset.dock===dock);b.setAttribute('aria-pressed',String(b.dataset.dock===dock))});
 let html='';
 if(dock==='files')html=filePreview?`<button class="back-files" data-action="back-files">${icon('folder')} 返回文件树</button><div class="subtitle"><span>${esc(filePreview)}</span><span>只读示例</span></div><pre class="code-preview">${files[filePreview].map((l,i)=>'<em>'+String(i+1).padStart(2,' ')+'</em>'+esc(l)).join('\n')}</pre><div class="note">说明性预览。原型不读取本机源码，也不提供编辑入口。</div>`:`<div class="subtitle"><span>资源管理器</span><span>只读浏览</span></div><div class="file-root">${icon('folder')}${current().workspace==='desktop'?'Dougong Desktop':'Dougong SDK'}</div><button class="folderbutton" data-folder="source" aria-expanded="true">${icon('chevron')}${icon('folder')}src</button><div class="filegroup" id="source"><button class="folderbutton" data-folder="framework" aria-expanded="true">${icon('chevron')}${icon('folder')}DougongDesktop.Framework</button><div id="framework">${['SessionComposer.cs','SessionRuntime.cs','IpcApprover.cs','Tools.cs'].map(n=>`<button class="filebutton ${n==='SessionComposer.cs'?'active':''}" data-file="${n}"><span class="file-icon">C#</span><span>${n}</span></button>`).join('')}</div><button class="folderbutton" data-folder="ipc" aria-expanded="false">${icon('right')}${icon('folder')}DougongDesktop.Ipc</button><div id="ipc" hidden><button class="filebutton" data-file="Protocol.cs"><span class="file-icon">C#</span><span>Protocol.cs</span></button></div><button class="folderbutton" data-folder="host" aria-expanded="false">${icon('right')}${icon('folder')}DougongDesktop.AgentHost</button><div id="host" hidden><button class="filebutton" data-file="Program.cs"><span class="file-icon">C#</span><span>Program.cs</span></button></div></div><div class="file-caption"><code>SessionComposer.cs</code>会话装配入口。选择文件查看说明性预览。<br><br>当前工作区是任务和权限的边界。</div>`;
 if(dock==='changes')html=`<div class="empty">${icon('diff')}<strong>本回合没有改动</strong><p>本期工具只浏览与读取文件。<br>改动面板保留，当前没有 diff 可查看。</p></div>`;
 if(dock==='plan')html=planHtml();
 if(dock==='terminal')html=`<div class="empty">${icon('terminal')}<strong>终端尚未实现</strong><p>本期保留面板位置。<br>原型不会启动 shell，也没有伪运行按钮。</p></div>`;
 $('#inspector-content').innerHTML=html;
}
function openDock(name){if(name==='context'){dispatch('context');return}dock=name;filePreview='';$('#app').classList.remove('no-inspector');renderDock()}
function renderPlugins(){
 const rows=plugins.filter(p=>p.type===pluginTab&&[p.name,p.description,p.identity].join(' ').toLowerCase().includes(searchPlugins.toLowerCase()));
 return `<section class="page"><div class="page-head"><div><h2>已安装的插件</h2><div class="desc">管理工作台的能力与外观。</div></div><button class="btn" data-action="load-local">${icon('plus')} 装载本地目录</button></div><div class="tabs"><button data-plugin-tab="agent" class="${pluginTab==='agent'?'active':''}">Agent 插件</button><button data-plugin-tab="ui" class="${pluginTab==='ui'?'active':''}">UI 插件</button></div><div class="plugin-toolbar"><input class="plugin-search" id="plugin-search" aria-label="搜索已安装插件" placeholder="搜索名称或插件 ID" value="${esc(searchPlugins)}"></div><div id="plugin-list">${rows.length?rows.map(pluginRow).join(''):`<div class="plugin-empty">${icon('plugin')}<strong>${searchPlugins?'没有匹配的插件':'还没有安装任何插件'}</strong><p>${searchPlugins?'尝试其他名称或清空搜索。':'从你信任的本地目录装载插件。'}</p></div>`}</div><div class="note">全部为演示条目。装载、启停和卸载只改变当前页面状态，不会更改本机文件。${pluginTab==='ui'?'<br>UI 扩展故障时保留内置界面；会话区域不可被第三方替换。':'<br>当前只管理本地插件，不提供插件市场。'}</div></section>`;
}
function pluginRow(p){return `<div class="plugin-row" data-plugin-row="${p.id}"><div><strong>${esc(p.name)}</strong><p class="plugin-desc">${esc(p.description)}</p><p class="plugin-id">${esc(p.identity)}</p><p>版本：演示 / 作者：${esc(p.author)} / 激活：${p.elapsed}</p>${p.error?`<p style="color:#974d44">${esc(p.error)}</p>`:''}</div><span class="badge ${p.error?'error':p.enabled?'good':'neutral'}">${p.error?'加载失败':p.enabled?'已启用':'已停用'}</span><button class="switch ${p.enabled?'':'off'}" role="switch" aria-checked="${p.enabled}" aria-label="启停${esc(p.name)}" data-toggle-plugin="${p.id}" ${p.id==='shell'||p.error?'disabled':''} title="${p.id==='shell'?'内置工作台始终保留':p.error?'请先检查插件错误':'切换启停'}"></button><button class="iconbtn" data-plugin-detail="${p.id}" aria-label="查看${esc(p.name)}详情" title="插件详情">${icon('info')}</button></div>`}
function renderSettings(){
 const tabs=[['providers','模型与提供商'],['appearance','布局与陪伴'],['diagnostics','诊断']];
 return `<section class="page"><h2>设置</h2><p class="desc">让工作台适应你的工作方式。</p><div class="tabs settingsnav">${tabs.map(([key,label])=>`<button class="${settingsTab===key?'active':''}" data-settings-tab="${key}">${label}</button>`).join('')}</div>${settingsTab==='providers'?providerForm():settingsTab==='appearance'?appearanceForm():diagnosticsForm()}</section>`;
}
function providerForm(){
 const p=provider(selectedProvider),locked=busy();
 return `<div class="provider-list">${providers.map(r=>`<button class="provider-item ${r.id===p.id?'active':''}" data-provider="${r.id}"><strong>${esc(r.name)}</strong><span>${r.id===defaultProvider?'新会话默认':esc(r.protocol)}</span></button>`).join('')}<button class="provider-new" data-action="add-provider">＋ 添加提供商</button></div><div class="form-grid"><label class="field">提供商名称<input id="provider-name" value="${esc(p.name)}" maxlength="40"></label><label class="field">协议<select id="protocol">${['OpenAI Chat Completions','OpenAI Responses','Anthropic Messages'].map(v=>`<option ${p.protocol===v?'selected':''}>${v}</option>`).join('')}</select></label><label class="field wide">Base URL<input id="base-url" type="url" value="${esc(p.url)}" placeholder="https://api.example.com/v1"></label><label class="field wide">模型 ID<input id="model-id" value="${esc(p.model)}" placeholder="输入自定义模型 ID"></label><label class="field">最大上下文（tokens）<input id="max-context" type="number" min="1" step="1" value="${p.context}"><span class="field-help">由你核对目标模型实际容量。</span></label><label class="field">最大输出（tokens）<input id="max-output" type="number" min="1" step="1" value="${p.output}"><span class="field-help">必须小于最大上下文。</span></label><label class="field wide">凭据来源<select id="credential"><option value="system" ${p.credential==='system'?'selected':''}>系统凭据管理器</option><option value="environment" ${p.credential==='environment'?'selected':''}>环境变量 DOUGONG_PROVIDER_KEY</option></select></label></div><div class="field-error" id="provider-error" role="alert"></div><div class="credential-state"><span>${p.credential==='environment'?'由环境变量提供（演示），不可编辑':'系统凭据：已配置（演示）'}</span><span>密钥不回显，原型不收集密钥</span></div><div class="form-actions"><button class="btn primary" data-action="save-provider">保存配置</button><button class="btn" data-action="test-connection">模拟测试连接</button><button class="btn" data-action="make-default" ${locked?'disabled':''}>设为新会话默认</button><button class="btn" data-action="delete-provider" ${providers.length===1?'disabled':''}>删除</button></div><div class="connection-result" id="connection-result" role="status"></div><div class="note">${locked?'当前回合进行中：只能保存草稿，不能更换活动提供商。':'提供商选择仅影响新建会话，已有会话保持原绑定。'}<br>这是内存中的配置演示，刷新后恢复初始示例。不会发送连接请求。</div>`;
}
function appearanceForm(){return `<section class="settings-section"><h3>阅读与布局</h3><div class="form-grid"><label class="field">阅读字号<select id="font-size">${[14,15,16].map(n=>`<option value="${n}" ${prefs.font===n?'selected':''}>${n} px</option>`).join('')}</select></label><label class="field">界面密度<select id="density"><option value="comfortable" ${prefs.density==='comfortable'?'selected':''}>舒适</option><option value="compact" ${prefs.density==='compact'?'selected':''}>紧凑</option></select></label></div><div class="note">桌面宽度下，拖动侧栏或右栏的分隔线调整宽度。布局与阅读偏好会保存在当前浏览器。</div><div class="form-actions"><button class="btn" data-action="reset-layout">恢复默认布局</button></div></section><section class="settings-section"><h3>榫灵陪伴</h3><div class="setting-row"><div>显示榫灵<small>隐藏陪伴角色，运行状态胶囊仍保留。</small></div><button id="pet-toggle" data-action="toggle-pet" class="switch ${prefs.pet?'':'off'}" role="switch" aria-checked="${prefs.pet}" aria-label="显示榫灵"></button></div><div class="setting-row"><div>减少动态效果<small>同时遵循系统设置；不使用持续跳跃提示。</small></div><button data-action="toggle-motion" class="switch ${prefs.motion?'':'off'}" role="switch" aria-checked="${prefs.motion}" aria-label="减少动态效果"></button></div><div class="form-actions"><button class="btn" data-action="pet-sleep">让榫灵休息</button><button class="btn" data-action="pet-wake">唤醒榫灵</button></div></section>`}
let diagnostics=false;
function diagnosticsForm(){return `<section class="settings-section"><h3>本地诊断</h3><p class="section-intro">记录会话、模型和工具状态，便于定位问题。</p><div class="setting-row"><div>启用诊断记录<small>正式应用写入本地 diagnostics.jsonl；原型仅演示开关。</small></div><button data-action="toggle-diagnostics" class="switch ${diagnostics?'':'off'}" role="switch" aria-checked="${diagnostics}" aria-label="启用诊断记录"></button></div><div class="context-legend"><div><span>当前工作区</span><span>本地目录</span></div><div><span>当前会话</span><span>${esc(current().title)}</span></div><div><span>工具审批等待</span><span>12.4 秒（模拟）</span></div><div><span>工具执行</span><span>${current().state==='completed'?'0.7 秒（模拟）':'—'}</span></div></div><div class="note">不记录密钥，不上传诊断数据。审批拒绝与工具失败分别显示。</div></section>`}
function modal(html,confirm){
 dialogReturn=document.activeElement;modalConfirm=confirm||null;$('#modal-host').innerHTML=`<div class="overlay" id="overlay"><div class="modal" role="dialog" aria-modal="true" aria-label="${html.includes('command-search')?'搜索与命令':'操作确认'}" tabindex="-1"><button class="iconbtn modal-close" data-action="close-modal" aria-label="关闭对话框">${icon('close')}</button>${html}</div></div>`;
 const target=$('#modal-host input:not([type=checkbox])')||$('#modal-host .modal');target.focus();
}
function closeModal(){$('#modal-host').innerHTML='';modalConfirm=null;if(dialogReturn?.isConnected)dialogReturn.focus()}
function commands(){
 modal(`<h3>搜索与命令</h3><input id="command-search" placeholder="搜索操作或会话…" aria-label="命令搜索"><div id="command-results"></div><p class="field-help">↑ ↓ 选择，Enter 执行，Esc 关闭</p>`);renderCommands('');
}
function renderCommands(q){
 const items=[['new','plus','新建会话','Ctrl N'],['plugins','plugin','打开插件管理',''],['settings','settings','打开设置',''],['review','layers','原型状态评审',''],...sessions.map(s=>['session:'+s.id,'file','切换到 '+s.title,'会话'])].filter(r=>r[2].toLowerCase().includes(q.toLowerCase()));
 $('#command-results').innerHTML=items.length?items.map(([key,ic,label,hint])=>`<button class="command" data-command="${key}">${icon(ic)}<span>${esc(label)}</span><small>${hint}</small></button>`).join(''):'<div class="empty-search">没有匹配的命令</div>';
}
function loadLocal(){modal(`<h3>装载本地插件目录</h3><p>从你信任的目录装载插件。原型不会实际读取该目录。</p><label>插件目录<input id="plugin-path" value="C:\\plugins\\Dougong.Sample.Plugin" aria-label="插件目录"></label><div class="notice warning">${icon('shield')}<div><strong>插件将在本机运行</strong><p>Agent 插件扩展能力，UI 插件可改变界面区域。只装载可信来源。</p></div></div><label class="checklabel"><input type="checkbox" id="plugin-trust">我了解并信任这个插件来源</label><div class="field-error" id="plugin-error" role="alert"></div><div class="modal-actions"><button class="btn" data-action="close-modal">取消</button><button class="btn primary" id="confirm-load" data-action="confirm-load" disabled>信任并模拟装载</button></div>`)}
function confirmLoad(){
 if(!$('#plugin-trust').checked)return;const path=$('#plugin-path').value.trim();if(!path){$('#plugin-error').textContent='请填写插件目录。';return}
 const p={id:'local-'+(++pluginCounter),name:'本地示例扩展 '+(pluginCounter-1),description:'从指定目录模拟装载，未读取实际文件。',identity:'local.sample.'+(pluginCounter-1),type:pluginTab,enabled:true,builtin:false,error:'',author:'本地演示',elapsed:'18 ms（模拟）',path};plugins.push(p);closeModal();searchPlugins='';renderView('plugins');toast('已完成模拟装载，可启停或卸载。');
}
function pluginDetail(id){const p=plugins.find(p=>p.id===id);if(!p)return;modal(`<h3>${esc(p.name)}</h3><p>${esc(p.description)}</p><div class="plugin-detail"><p>类型：${p.type==='ui'?'UI 插件':'Agent 插件'}</p><p>标识：<code>${esc(p.identity)}</code></p><p>状态：${p.error?'加载失败':p.enabled?'已启用':'已停用'}</p><p>激活耗时：${p.elapsed}</p>${p.path?'<p>目录：'+esc(p.path)+'</p>':''}${p.error?'<p style="color:#974d44">'+esc(p.error)+'</p>':''}</div><p>${p.builtin?'这是内置组件，保留以确保工作台可使用。':'卸载会释放插件占用的内容，原型不删除文件。'}</p><div class="modal-actions"><button class="btn" data-action="close-modal">关闭</button>${!p.builtin?`<button class="btn" data-uninstall="${p.id}">卸载插件</button>`:''}</div>`)}
function uninstall(id){const p=plugins.find(p=>p.id===id);if(!p||p.builtin)return;modal(`<h3>卸载 ${esc(p.name)}？</h3><p>插件占用的界面内容或工具将不再可用。此处只更新演示数据，卸载后可再次装载。</p><div class="modal-actions"><button class="btn" data-action="close-modal">取消</button><button class="btn primary" id="confirm-uninstall" data-action="confirm-modal">确认模拟卸载</button></div>`,()=>{plugins.splice(plugins.indexOf(p),1);renderView('plugins');toast('已从演示列表卸载。')})}
function validateProvider(){
 const value={name:$('#provider-name').value.trim(),url:$('#base-url').value.trim(),model:$('#model-id').value.trim(),context:Number($('#max-context').value),output:Number($('#max-output').value),protocol:$('#protocol').value,credential:$('#credential').value};
 let error='',field='';if(!value.name){error='请填写提供商名称。';field='provider-name'}else if(!/^https?:\/\/[^\s/]+/i.test(value.url)){error='Base URL 需要以 http:// 或 https:// 开头。';field='base-url'}else if(!value.model){error='请填写模型 ID。';field='model-id'}else if(!Number.isInteger(value.context)||value.context<=0){error='最大上下文需要为正整数。';field='max-context'}else if(!Number.isInteger(value.output)||value.output<=0||value.output>=value.context){error='最大输出需要为正整数，且小于最大上下文。';field='max-output'}
 $$('.form-grid input').forEach(e=>e.removeAttribute('aria-invalid'));$('#provider-error').textContent=error;if(error){$('#'+field).setAttribute('aria-invalid','true');$('#'+field).focus();return null}return value;
}
function saveProvider(){const v=validateProvider();if(!v)return false;Object.assign(provider(selectedProvider),v);toast('已保存演示配置；已有会话保持原绑定。');renderView('settings');return true}
function addProvider(){const id='provider-'+(++providerCounter);providers.push({id,name:'新的提供商',protocol:'OpenAI Chat Completions',url:'https://api.example.com/v1',model:'',context:128000,output:8192,credential:'system'});selectedProvider=id;renderView('settings');$('#provider-name').focus()}
function deleteProvider(){const p=provider(selectedProvider);if(providers.length<2)return;if(sessions.some(s=>s.provider===p.id)){toast('该提供商已绑定演示会话。请保留它，或删除尚未使用的配置。');return}modal(`<h3>删除这个提供商？</h3><p>${esc(p.name)} 将从本页配置中移除。不会删除系统凭据。</p><div class="modal-actions"><button class="btn" data-action="close-modal">取消</button><button class="btn primary" data-action="confirm-modal">确认删除</button></div>`,()=>{providers.splice(providers.indexOf(p),1);if(defaultProvider===p.id)defaultProvider=providers[0].id;selectedProvider=providers[0].id;renderView('settings')})}
function renderOnboarding(){return `<section class="page"><div class="onboarding"><div class="onboarding-head"><img src="${logo}" alt="Dougong"><div><h2>准备好你的工作台</h2><p>连接一个模型，选定工作区，就可以开始。</p></div></div><section class="onboarding-step"><h3>选择工作区</h3><p>工作区决定任务可以访问的目录范围。</p><label class="field">工作区目录<input value="C:\\work\\myProject\\Dougong Desktop" readonly aria-label="工作区目录"></label></section><section class="onboarding-step"><h3>配置模型与凭据</h3><p>正式应用通过系统凭据管理器保存密钥。本原型只演示配置过程，不收集 API Key。</p><div class="form-grid"><label class="field">提供商名称<input id="onboard-name" value="本地开发配置"></label><label class="field">模型 ID<input id="onboard-model" value="example-model"></label><label class="field wide">Base URL<input id="onboard-url" value="https://api.example.com/v1"></label></div><div class="credential-state" style="margin-top:16px"><span>系统凭据管理器</span><span>待配置（模拟）</span></div></section><div class="form-actions"><button class="btn primary" data-action="finish-onboarding">完成模拟配置</button><button class="btn" data-view="conversation">稍后设置</button></div><p class="field-help">首次引导完成后可显示榫灵，随时可以在设置中隐藏。</p></div></section>`}
function renderRecovery(){return `<section class="page"><div class="recovery"><h2>接着上次的工作</h2><p>演示会话已保留。恢复时先同步最新快照，不自动重新执行工具。</p><div class="recovery-card"><span class="badge neutral">本地会话</span><h3 style="margin-top:14px">${esc(current().title)}</h3><p>工作区：Dougong Desktop<br>最后状态：结果待核实<br>最近更新：今天，10:24（演示）</p><div class="form-actions"><button class="btn primary" data-action="restore-session">恢复会话</button><button class="btn" data-action="recovery-new">新建会话</button></div></div></div></section>`}
function review(){const options=[['pending','待审批','四档决策与输入锁'],['thinking','运行中','计时、停止与步骤'],['completed','完成','结果、计划与工具输出'],['expired','审批过期','旧授权不可继续使用'],['unknown','结果未知','人工核实，不自动重放'],['failed','工具失败','显示失败原因'],['disconnected','引擎断开','保留现场，先同步状态'],['compacted','上下文压缩','有损提示与用量变化'],['notCompacted','压缩未成功','保留原有上下文'],['plugin-failed','插件故障','UI 回退内置界面'],['onboarding','首次启动','工作区和模型配置'],['recovery','会话恢复','未知结果不自动重放']];modal(`<h3>原型状态评审</h3><p class="review-note">仅供评审切换场景，不是产品内的运行功能。会重置当前示例状态，不执行真实操作。</p><div class="review-grid">${options.map(([id,title,desc])=>`<button data-scenario="${id}"><strong>${title}</strong><small>${desc}</small></button>`).join('')}</div>`)}
function scenario(name){
 closeModal();const s=current();
 if(name==='onboarding'||name==='recovery'){renderView(name);return}
 if(name==='plugin-failed'){if(!plugins.find(p=>p.id==='fault'))plugins.push({id:'fault',name:'第三方布局扩展',description:'用于评审故障隔离的示例 UI 插件。',identity:'demo.ui.layout',type:'ui',enabled:false,builtin:false,error:'加载失败：示例依赖未找到。已回退到内置清晰工作台。',author:'演示作者',elapsed:'—'});pluginTab='ui';searchPlugins='';renderView('plugins');return}
 s.started=true;s.task=s.task||sessions[0].task;s.elapsed=12.4;s.messages=[];s.decision='';s.notice='';
 if(name==='compacted'||name==='notCompacted'){s.state='completed';s.notice=name;s.decision='once'}else{s.state=name;if(name==='completed')s.decision='once'}
 renderView('conversation');
 if(name==='thinking')setTimeout(()=>{if(s.state==='thinking'){s.state='executing';if(current()===s)renderView()}},1300);
}
function switchSession(id){const s=sessions.find(s=>s.id===id);if(!s)return;active=id;filePreview='';renderView('conversation');closeModal()}
function executeCommand(command){closeModal();if(command==='new')newSession();else if(command==='review')review();else if(command.startsWith('session:'))switchSession(command.slice(8));else renderView(command)}
function reconnect(){const s=current();s.state='unknown';renderView('conversation');toast('已模拟重新连接并同步快照；原工具结果仍未知，没有重放。')}
function dispatch(action){
 switch(action){
 case 'send':sendMessage();break;
 case 'cancel-turn':current().state='stopped';renderView();break;
 case 'new':newSession();break;
 case 'starter':sendMessage('帮我梳理项目结构和会话的装配方式。');break;
 case 'model':if(busy())toast('当前回合绑定 '+provider(current().provider).name+'，不能在运行中切换。');else{settingsTab='providers';selectedProvider=current().provider;renderView('settings')}break;
 case 'commands':commands();break;
 case 'context':contextOpen=!contextOpen;if($('#context-popover')){$('#context-popover').classList.toggle('hidden',!contextOpen);$('#context-trigger').setAttribute('aria-expanded',String(contextOpen))}break;
 case 'close-context':contextOpen=false;$('#context-popover')?.classList.add('hidden');$('#context-trigger')?.setAttribute('aria-expanded','false');$('#context-trigger')?.focus();break;
 case 'review':review();break;
 case 'close-modal':closeModal();break;
 case 'confirm-modal':{const fn=modalConfirm;closeModal();fn?.();break}
 case 'load-local':loadLocal();break;
 case 'confirm-load':confirmLoad();break;
 case 'save-provider':saveProvider();break;
 case 'add-provider':addProvider();break;
 case 'delete-provider':deleteProvider();break;
 case 'make-default':if(busy()){toast('当前回合进行中，不能切换提供商。');break}if(saveProvider()){defaultProvider=selectedProvider;renderView('settings');toast('新建会话将使用这个提供商。')}break;
 case 'test-connection':if(validateProvider())$('#connection-result').innerHTML='<span class="badge good">模拟连接成功</span> 未发送网络请求；正式应用会显示实际失败原因。';break;
 case 'toggle-pet':prefs.pet=!prefs.pet;plugins.find(p=>p.id==='pet').enabled=prefs.pet;savePrefs();renderView();break;
 case 'toggle-motion':prefs.motion=!prefs.motion;savePrefs();renderView();break;
 case 'pet-sleep':petState('sleeping');toast('榫灵休息了；运行状态仍然可见。');break;
 case 'pet-wake':petState('idle');break;
 case 'toggle-diagnostics':diagnostics=!diagnostics;renderView();break;
 case 'reset-layout':Object.assign(prefs,{left:244,right:300,collapsed:false,font:14,density:'comfortable'});savePrefs();renderView();break;
 case 'back-files':filePreview='';renderDock();break;
 case 'reconnect':reconnect();break;
 case 'restore-session':current().state='unknown';current().task=current().task||sessions[0].task;current().started=true;renderView('conversation');toast('已恢复模拟快照。结果未知的工具不会自动重试。');break;
 case 'recovery-new':current().state='stopped';newSession();break;
 case 'finish-onboarding':if(!$('#onboard-name').value.trim()||!$('#onboard-model').value.trim()||!/^https?:\/\//.test($('#onboard-url').value)){toast('请填写提供商名称、模型和有效的 Base URL。');break}prefs.pet=true;savePrefs();renderView('conversation');toast('模拟配置完成，未写入凭据或连接模型。');break;
 }
}
function init(){
 $('nav.dock [data-dock="context"]')?.remove();$('.inspector-bottom')?.remove();
 const old=$('.sidebar .section-title');const group=$('.sidebar .workspace');old?.remove();group?.remove();const scroll=document.createElement('div');scroll.className='workspace-scroll';$('.sidebar-space').before(scroll);
 $('#status-capsule').setAttribute('data-action','status');$('#reset-demo').setAttribute('aria-label','重置当前示例');$('#reset-demo').title='重置当前示例';$('#prototype-info').setAttribute('aria-label','原型状态评审');$('#prototype-info').title='原型状态评审';$('#command-open').setAttribute('aria-label','搜索与命令');
 $('#collapse-sidebar').setAttribute('aria-label','收起侧栏');$('#close-inspector').setAttribute('aria-label','收起右栏');
 const resizeL=document.createElement('div');resizeL.className='resize-handle left';resizeL.dataset.resize='left';resizeL.title='拖动调整侧栏宽度';$('#app').append(resizeL);const resizeR=document.createElement('div');resizeR.className='resize-handle right';resizeR.dataset.resize='right';resizeR.title='拖动调整右栏宽度';$('#app').append(resizeR);
 if(innerWidth<=900)$('#app').classList.add('no-inspector');renderView();
}
document.addEventListener('click',e=>{
 if(contextOpen&&!e.target.closest('.context-area')){contextOpen=false;$('#context-popover')?.classList.add('hidden');$('#context-trigger')?.setAttribute('aria-expanded','false')}
 if(e.target.id==='overlay'){closeModal();return}const b=e.target.closest('button');if(!b||b.disabled)return;
 if(b.dataset.decision){decide(b.dataset.decision);return}
 if(b.dataset.view){renderView(b.dataset.view);closeModal();return}
 if(b.dataset.dock){openDock(b.dataset.dock);return}
 if(b.dataset.session){switchSession(b.dataset.session);return}
 if(b.dataset.workspace){const list=$(`[data-workspace-list="${b.dataset.workspace}"]`);list.hidden=!list.hidden;b.setAttribute('aria-expanded',String(!list.hidden));return}
 if(b.dataset.folder){const div=$('#'+b.dataset.folder);div.hidden=!div.hidden;b.setAttribute('aria-expanded',String(!div.hidden));b.querySelector('svg').outerHTML=icon(div.hidden?'right':'chevron');return}
 if(b.dataset.file){filePreview=b.dataset.file;renderDock();return}
 if(b.dataset.pluginTab){pluginTab=b.dataset.pluginTab;searchPlugins='';renderView();return}
 if(b.dataset.settingsTab){settingsTab=b.dataset.settingsTab;renderView();return}
 if(b.dataset.provider){selectedProvider=b.dataset.provider;renderView();return}
 if(b.dataset.togglePlugin){const p=plugins.find(p=>p.id===b.dataset.togglePlugin);if(!p||p.id==='shell'||p.error)return;p.enabled=!p.enabled;if(p.id==='pet'){prefs.pet=p.enabled;savePrefs()}renderView();toast('已模拟'+(p.enabled?'启用':'停用')+'插件。');return}
 if(b.dataset.pluginDetail){pluginDetail(b.dataset.pluginDetail);return}
 if(b.dataset.uninstall){uninstall(b.dataset.uninstall);return}
 if(b.dataset.command){executeCommand(b.dataset.command);return}
 if(b.dataset.scenario){scenario(b.dataset.scenario);return}
 if(b.dataset.action&&b.dataset.action!=='status'){dispatch(b.dataset.action);return}
 switch(b.id){
 case 'new-session':newSession();break;
 case 'command-open':commands();break;
 case 'workspace-info':toast('当前工作区是任务和权限的边界；原型不访问真实目录。');break;
 case 'status-capsule':statusOpen=!statusOpen;updateStatus();break;
 case 'collapse-sidebar':prefs.collapsed=!prefs.collapsed;savePrefs();break;
 case 'close-inspector':$('#app').classList.add('no-inspector');break;
 case 'reset-demo':scenario('pending');break;
 case 'prototype-info':review();break;
 }
});
document.addEventListener('input',e=>{
 if(e.target.id==='command-search')renderCommands(e.target.value);
 if(e.target.id==='plugin-search'){searchPlugins=e.target.value;const pos=e.target.selectionStart;$('#content').innerHTML=renderPlugins();$('#plugin-search').focus();$('#plugin-search').setSelectionRange(pos,pos)}
 if(e.target.id==='plugin-trust')$('#confirm-load').disabled=!e.target.checked;
});
document.addEventListener('change',e=>{
 if(e.target.id==='density'){prefs.density=e.target.value;savePrefs()}
 if(e.target.id==='font-size'){prefs.font=Number(e.target.value);savePrefs()}
 if(e.target.id==='credential'){const p=provider(selectedProvider);p.credential=e.target.value;$('.credential-state').innerHTML='<span>'+(p.credential==='environment'?'由环境变量提供（演示），不可编辑':'系统凭据：已配置（演示）')+'</span><span>密钥不回显，原型不收集密钥</span>'}
});
document.addEventListener('keydown',e=>{
 if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='k'){e.preventDefault();commands();return}
 if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='n'){e.preventDefault();newSession();return}
 if(e.key==='Escape'){if(contextOpen){contextOpen=false;$('#context-popover')?.classList.add('hidden');$('#context-trigger')?.setAttribute('aria-expanded','false');$('#context-trigger')?.focus()}closeModal();return}
 if(e.target.id==='message'&&e.key==='Enter'&&!e.shiftKey&&!e.isComposing){e.preventDefault();sendMessage();return}
 const dialog=$('#modal-host .modal');if(!dialog)return;
 if(e.key==='Tab'){const focusable=Array.from(dialog.querySelectorAll('button:not(:disabled),input:not(:disabled),select,[tabindex="0"]')).filter(el=>el.getBoundingClientRect().height);const first=focusable[0],last=focusable.at(-1);if(e.shiftKey&&document.activeElement===first){e.preventDefault();last.focus()}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first.focus()}}
 if(['ArrowDown','ArrowUp'].includes(e.key)&&$('#command-search')){const buttons=$$('[data-command]');if(!buttons.length)return;e.preventDefault();const n=buttons.indexOf(document.activeElement),next=e.key==='ArrowDown'?(n+1)%buttons.length:(n-1+buttons.length)%buttons.length;buttons[next].focus()}
 if(e.key==='Enter'&&e.target.id==='command-search'){$('[data-command]')?.click()}
});
let drag=null;
document.addEventListener('pointerdown',e=>{
 const handle=e.target.closest('[data-resize]');if(handle&&innerWidth>900){drag={type:handle.dataset.resize,x:e.clientX,start:handle.dataset.resize==='left'?prefs.left:prefs.right};handle.setPointerCapture(e.pointerId);handle.classList.add('dragging');document.body.classList.add('layout-drag');return}
 if(e.target.matches('#pet-place img')){drag={type:'pet',restore:petRestore};e.target.setPointerCapture(e.pointerId);petState('dragged')}
});
document.addEventListener('pointermove',e=>{if(!drag||drag.type==='pet')return;const delta=e.clientX-drag.x;if(drag.type==='left')prefs.left=Math.max(200,Math.min(320,drag.start+delta));else prefs.right=Math.max(250,Math.min(420,drag.start-delta));applyPrefs()});
function endDrag(){if(!drag)return;if(drag.type==='pet')petState(drag.restore==='dragged'?'idle':drag.restore);else{savePrefs();$$('.resize-handle').forEach(e=>e.classList.remove('dragging'));document.body.classList.remove('layout-drag')}drag=null}
document.addEventListener('pointerup',endDrag);document.addEventListener('pointercancel',endDrag);
setInterval(()=>{sessions.forEach(s=>{if(['thinking','executing'].includes(s.state))s.elapsed=Math.round((s.elapsed+.1)*10)/10});if(['thinking','executing'].includes(current().state))$('#elapsed').textContent=current().elapsed.toFixed(1)+' 秒'},100);
let idleTimer;function resetIdle(){clearTimeout(idleTimer);if(petRestore==='sleeping'&&current().state==='idle')petState('idle');idleTimer=setTimeout(()=>{if(['idle','completed'].includes(current().state))petState('sleeping')},300000)}
document.addEventListener('keydown',resetIdle);document.addEventListener('pointerdown',resetIdle);init();resetIdle();
const query=new URLSearchParams(location.search);
if(['plugins','settings','onboarding','recovery'].includes(query.get('view'))){if(['providers','appearance','diagnostics'].includes(query.get('tab')))settingsTab=query.get('tab');renderView(query.get('view'))}
if(['pending','thinking','completed','expired','unknown','failed','disconnected','compacted','notCompacted','plugin-failed','onboarding','recovery'].includes(query.get('scenario')))scenario(query.get('scenario'));
