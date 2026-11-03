export const labels = {
  Name: '标题',
  Overview: '简介',
  Genres: '分类',
  queued: '排队中',
  running: '处理中',
  waiting: '等待中',
  paused: '已暂停',
  failed: '失败',
  completed: '完成',
  expired: '已失效',
  cancelled: '已取消',
  source: '读取原文',
  reuse: '复用译文',
  request: '请求模型',
  save: '保存译文',
  display: '更新显示',
  verify: '核实显示',
  dependency: '等待分类词条',
  rate_limit: '等待限流',
  translation: '翻译',
  fill: '补齐',
  language: '语言切换',
  retranslate: '明确重译',
  manual: '人工操作',
  dictionary: '词典',
  execution: '执行异常',
  recovery: '处理中断恢复',
  Term: '词条',
  connection_test: '连接测试',
  added: '新增影片',
  nfo: '原文变化',
};
export const messages = {
  LocalRulesInvalid: {
    zh: '本机规则文件无效，请修正后重启 Jellyfin。',
    en: 'Invalid local rules file. Correct it and restart Jellyfin.',
  },
  RetryIntentExpired: {
    zh: '任务已过期，请刷新处理记录。',
    en: 'This task is no longer current. Refresh the task list.',
  },
  executor_database_error: {
    zh: '数据库暂时不可用，正在恢复',
    en: 'The database is temporarily unavailable. Recovery is in progress.',
  },
  executor_io_error: {
    zh: '存储暂时不可用，正在恢复',
    en: 'Storage is temporarily unavailable. Recovery is in progress.',
  },
  executor_interrupted: {
    zh: '任务中断，正在恢复',
    en: 'The task was interrupted. Recovery is in progress.',
  },
  RevisionConflict: {
    zh: '内容或全局目标已变化，请刷新后核对。草稿仍保留。',
    en: 'Content or the global target has changed. Refresh and review; your draft is preserved.',
  },
  SubmissionConflict: {
    zh: '提交标识已用于另一操作，请刷新后重试。',
    en: 'This submission ID belongs to another operation. Refresh and try again.',
  },
  PreviewExpired: {
    zh: '预览已过期，请重新预览。',
    en: 'The preview has expired. Create a new preview.',
  },
  UnknownOutcomeConsentRequired: {
    zh: '此前请求结果未知，需要明确接受可能重复计费的风险。',
    en: 'An earlier request has an unknown outcome. Explicitly accept possible duplicate charges before retrying.',
  },
  UnifiedDatabaseRequired: {
    zh: '请先创建新版数据库。旧版数据保持原样。',
    en: 'Create the new database first. Older data stays untouched.',
  },
  UnsupportedUnifiedSchema: {
    zh: '数据库定义与当前插件不匹配。',
    en: 'The database definition is incompatible with this plugin.',
  },
  invalid_request: {
    zh: '输入格式有误，请检查后重试。',
    en: 'Invalid input. Check the values and try again.',
  },
  person_template_required: {
    zh: '旧译文缺少安全的人名位置，请手动编辑或明确重新翻译。',
    en: 'This older translation has no verified name positions. Edit it manually or explicitly retranslate.',
  },
  person_template_invalid: {
    zh: '人名位置记录无效，原文与已有译文已保留，请核对后处理。',
    en: 'The saved name positions are invalid. Original text and existing translations are preserved. Review them before continuing.',
  },
  person_alias_conflict: {
    zh: '人名原名、别名或历史译名存在冲突，请先核对并修正。',
    en: 'Person names, aliases or historical display names conflict. Check and correct the rules first.',
  },
  input_outcome_unknown: {
    zh: '同一原文此前请求结果未知，重试前需确认可能重复计费的风险。',
    en: 'An earlier request for this source has an unknown outcome. Accept the possible duplicate-charge risk before retrying.',
  },
  operation_unavailable: {
    zh: '操作暂时不可用，请检查连接、来源和实例状态。',
    en: 'The operation is temporarily unavailable. Check the connection, source and server state.',
  },
  network_uncertain: {
    zh: '请求结果未知',
    en: 'Request outcome unknown',
  },
  authentication: {
    zh: '连接鉴权失败',
    en: 'Connection authentication failed',
  },
  permission: {
    zh: '连接权限不足',
    en: 'Connection permission denied',
  },
  quota: {
    zh: '服务额度不足',
    en: 'Service quota exhausted',
  },
  credential_unavailable: {
    zh: '密钥不可用',
    en: 'The key is unavailable',
  },
  field_locked: {
    zh: '字段已锁定',
    en: 'The field is locked',
  },
  metadata_savers_not_disabled: {
    zh: '媒体库元数据保存器尚未关闭',
    en: 'Metadata savers are still enabled for this library',
  },
  source_binding_changed: {
    zh: '媒体路径或来源绑定已变化',
    en: 'The media path or source binding has changed',
  },
  source_unavailable: {
    zh: '暂时无法读取原文',
    en: 'The original text is temporarily unavailable',
  },
  display_conflict: {
    zh: '当前显示已被外部修改，已保留',
    en: 'The display was changed externally and has been preserved',
  },
  original_title_unavailable: {
    zh: '需要核对标题原文',
    en: 'Review and confirm the original title',
  },
  invalid_output: {
    zh: '响应内容不可用',
    en: 'The response content is unusable',
  },
  rate_limit: {
    zh: '服务限流',
    en: 'Service rate limit',
  },
  source_observation_failed: {
    zh: '来源观察失败，稍后重试',
    en: 'Source observation failed; it will retry later',
  },
  executor_failed: {
    zh: '执行器出现异常，持久化记录已保留',
    en: 'The executor encountered an error. Persisted records are preserved',
  },
  OutsideManagedScope: {
    zh: '该影片或字段不在全局管理范围内。',
    en: 'This movie or field is outside managed scope.',
  },
  external_display_preserved: {
    zh: '保留外部显示',
    en: 'External display preserved',
  },
  model_catalog_unavailable: {
    zh: '模型列表暂时不可用',
    en: 'Model catalog is unavailable',
  },
  output_budget: {
    zh: '输出达到上限',
    en: 'Output limit reached',
  },
  incomplete_output: {
    zh: '模型输出未完整结束',
    en: 'Model output did not finish',
  },
  overview_invalid_json: {
    zh: '简介 JSON 格式不正确',
    en: 'Invalid overview JSON',
  },
  overview_expected_object: {
    zh: '简介响应应为对象',
    en: 'Overview response must be an object',
  },
  overview_field_missing: {
    zh: '响应缺少 overview 字段',
    en: 'Missing overview field',
  },
  overview_expected_string: {
    zh: 'overview 字段应为文本',
    en: 'Overview field must be text',
  },
  overview_empty_text: {
    zh: '简介为空',
    en: 'Empty overview',
  },
  overview_text_too_long: {
    zh: '简介超过字符上限',
    en: 'Overview exceeds character limit',
  },
  overview_invalid_character: {
    zh: '简介含无效字符',
    en: 'Invalid character in overview',
  },
  overview_names_missing: {
    zh: '简介遗漏受保护人名',
    en: 'Protected name missing',
  },
  overview_names_duplicated: {
    zh: '简介重复受保护人名',
    en: 'Protected name duplicated',
  },
  overview_names_unexpected: {
    zh: '简介出现未知人名占位符',
    en: 'Unexpected name marker',
  },
  overview_unresolved_marker: {
    zh: '简介仍有未还原占位符',
    en: 'Unresolved overview marker',
  },
  overview_invalid_text: {
    zh: '简介文本无效',
    en: 'Invalid overview text',
  },
  overview_output_truncated: {
    zh: '简介输出被截断',
    en: 'Truncated overview output',
  },
  overview_accepted: {
    zh: '简介校验通过',
    en: 'Overview validation passed',
  },
};
export const errors = Object.fromEntries(
  Object.entries(messages).map(([code, text]) => [code, text.zh]),
);
const interfaceWords = {
  本机规则已加载: 'Local rules loaded',
  使用内置通用规则: 'Using built-in general rules',
  '开始使用 Metadata Localizer': 'Get started with Metadata Localizer',
  '新版使用独立数据目录。创建数据库后，选择管理的媒体库并配置连接。':
    'This version uses a separate data directory. Create its database, choose managed libraries, and configure a connection.',
  '检测到旧版或其他分支数据。它们保留在原目录，新版不会导入或覆盖这些数据。请先保留旧版备份。':
    'Older or other-branch data was detected. It remains in its original directory and is not imported or overwritten. Keep a backup of the older installation.',
  创建新版数据库: 'Create new database',
  '正在查看 · 已移出当前分页': 'Reading · outside the current page',
  取消确认: 'Cancel confirmation',
  '任务已变化，请重新核对后重试。': 'The task has changed. Review it before retrying.',
  最近任务: 'Recent tasks',
  提交时间: 'Submitted at',
  标题和词条输出上限: 'Title and term output limit',
  简介输出上限: 'Overview output limit',
  '简介默认至少 4096 Token；仅作为输出上限，按实际用量计费。':
    'Overview defaults to at least 4096 tokens. This is a ceiling; billing uses actual usage.',
  '补齐仅处理当前目标语言的缺译字段。已有译文、空原文、保留或保护及处理中项会跳过。缺译或明确重译会发送原文；分类复用共享词典。':
    'Fill only handles missing translations in the current target language. Existing translations, empty sources, kept or protected fields, and active work are skipped. Missing or explicitly retranslated fields send original text; genres reuse the shared dictionary.',
  '正在检查重译范围…': 'Checking retranslation scope…',
  '正在检查缺译，仅检查缺失字段…': 'Checking missing fields…',
  仅补缺译: 'Fill missing only',
  '个字段；跳过': 'fields; skipped',
  '个已有译文、空原文、保留或保护及处理中字段。':
    'existing, empty, kept, protected or active fields.',
  '个原文已变化，等待后台观察后再补齐。':
    'sources changed; wait for background observation before filling.',
  '此前失败且仍缺译的字段会再次尝试。':
    'Previously failed fields that are still missing will be attempted again.',
  '当前没有可补齐的缺译字段。': 'No missing fields are eligible for filling.',
  搜索人名或别名: 'Search names or aliases',
  没有匹配的人名规则: 'No matching person rules',
  问题已解决或目前没有对应项目: 'Resolved or no related movies currently',
  查看影片: 'View movie',
  校验详情: 'Validation details',
  '标题、原文或译文': 'Title, original or translation',
  '连接已保存；全局设置尚未保存。草稿保留：':
    'Connection saved; global settings are not saved. Draft retained: ',
  待处理: 'Pending',
  问题: 'Issues',
  选择影片: 'Select movie',
  暂无记录: 'No records',
  已读取: 'Loaded',
  '个模型。未发送翻译请求。': 'models. No translation request was sent.',
  留空保留现有密钥: 'Leave blank to keep the saved key',
  '保存器已开启，显示更新将被阻止': 'Metadata savers are enabled; display writes will be blocked',
  当前阶段: 'Current stage',
  '原文、译文与当前显示': 'Originals, translations and current display',
  '全局目标 · 简体中文': 'Global target · Simplified Chinese',
  '全局目标 · English': 'Global target · English',
  统一数据库尚未就绪: 'Unified database is not ready',
  全新空实例: 'New empty instance',
  '只在没有需要迁移的数据时创建空数据库。':
    'Create an empty database only when there is no existing data to import.',
  创建空数据库: 'Create empty database',
  浏览媒体库: 'Browse library',
  当前进度: 'Current progress',
  刷新: 'Refresh',
  补齐管理范围: 'Fill managed scope',
  '保留原文和空字段不计入覆盖分母。': 'Kept originals and empty fields are excluded from coverage.',
  '优先复用已有译文；可用结果自动显示。':
    'Reuse saved translations first; usable results display automatically.',
  处理记录: 'Processing history',
  查看处理明细: 'View work details',
  需要处理的问题: 'Items needing attention',
  暂无需要处理的问题: 'No items need attention',
  暂无处理记录: 'No processing history',
  概览: 'Overview',
  影片: 'Movies',
  设置: 'Settings',
  标题: 'Title',
  简介: 'Synopsis',
  分类: 'Genres',
  缺译: 'Missing',
  待显示: 'Pending display',
  保留原文: 'Keep original',
  '已有相同任务，已复用。': 'An existing task is already processing this item.',
  完整原文与结果: 'Full original and result',
  未计量: 'Not measured',
  查看问题: 'View issues',
  查看词条任务: 'View term task',
  搜索: 'Search',
  全部已观察影片: 'All observed movies',
  筛选: 'Filter',
  全部影片: 'All movies',
  有缺译: 'Missing translations',
  存在问题: 'Has issues',
  有人工版本: 'Has manual versions',
  '部影片 · 已选': 'movies · selected',
  补齐选中影片: 'Fill selected movies',
  重译选中影片: 'Retranslate selected',
  上一页: 'Previous',
  下一页: 'Next',
  没有匹配的已观察影片: 'No matching observed movies',
  处理中: 'Processing',
  来源问题: 'Source issues',
  获取模型列表: 'Get model list',
  请选择模型: 'Choose a model',
  '填写 API 地址和 Key，获取列表后选择模型并保存。翻译测试可选。':
    'Enter the API base and key, get the list, choose a model, then save. Translation testing is optional.',
  '连接草稿已变化，请重新获取模型列表。': 'The connection draft changed. Get the model list again.',
  全局设置: 'Global settings',
  有未保存修改: 'Unsaved changes',
  已保存: 'Saved',
  放弃修改: 'Discard changes',
  保存设置: 'Save settings',
  语言与处理范围: 'Language and scope',
  全局目标语言: 'Global target language',
  简体中文: 'Simplified Chinese',
  管理媒体库: 'Managed libraries',
  自动处理新增影片和原文变化: 'Process new movies and source changes automatically',
  '新加入的媒体库先建立观察基线。历史缺译可从概览主动补齐；浏览媒体库不会改变这里的管理范围。':
    'New managed libraries establish a baseline first. Fill older missing translations from Overview. Browsing a library does not change managed scope.',
  模型连接: 'Model connection',
  'HTTPS API 地址': 'HTTPS API base',
  模型: 'Model',
  '整次请求期限（秒）': 'Whole request deadline (seconds)',
  每分钟最多请求: 'Requests per minute',
  请求参数: 'Request parameters',
  '最大输出 Token': 'Maximum output tokens',
  '使用 JSON 响应格式': 'Use JSON response format',
  '更换模型或参数后仍优先复用已存译文。只有明确重译才重新请求。':
    'Changing the model or parameters preserves reuse. Only explicit retranslation sends a new request.',
  读取模型列表: 'Load model list',
  翻译固定示例词测试连接: 'Test with a fixed example word',
  分类词典: 'Genre dictionary',
  查找: 'Find',
  添加词条: 'Add term',
  人名规则: 'Person rule',
  '更改规则不发模型请求，在下次同步显示时应用。':
    'Rule edits send no model request; they apply on the next display sync.',
  添加人名: 'Add person',
  关闭: 'Close',
  '草稿尚未保存。': 'The draft is not saved.',
  放弃草稿并关闭: 'Discard draft and close',
  原文: 'Original',
  当前显示: 'Current display',
  人工译文: 'Manual translation',
  '（JSON 字符串数组）': '(JSON string array)',
  只保存: 'Save only',
  保存并显示: 'Save and show',
  恢复原文: 'Restore original',
  跟随全局目标: 'Follow global target',
  重译此字段: 'Retranslate this field',
  译文历史: 'Translation history',
  历史语言: 'History language',
  原文版本: 'Source version',
  选用并显示: 'Select and show',
  核对标题原文与前缀: 'Verify title source and prefix',
  可信标题原文: 'Trusted title source',
  显示前缀: 'Display prefix',
  保存来源绑定: 'Save source binding',
  '原文为空。': 'The original is empty.',
  确认处理范围: 'Confirm scope',
  开始处理: 'Start',
  确认保存并切换: 'Save and switch',
  切换全局目标至: 'Switch global target to',
  '个字段，可复用': 'fields; reusable',
  '，保留原文': '; kept originals',
  '，缺译': '; missing',
  '。缺译项会请求模型，两种语言历史都会保留。':
    '. Missing translations use the model; both language histories are retained.',
  'Token 参数': 'Token parameter',
  处理明细: 'Work details',
  排队中: 'Queued',
  等待中: 'Waiting',
  已暂停: 'Paused',
  失败: 'Failed',
  完成: 'Completed',
  已失效: 'Expired',
  已取消: 'Cancelled',
  读取原文: 'Read source',
  复用译文: 'Reuse translation',
  请求模型: 'Model request',
  保存译文: 'Save translation',
  更新显示: 'Update display',
  核实显示: 'Verify display',
  重试失败项: 'Retry failed item',
  请求记录: 'Request attempts',
  请求: 'Request',
  保存: 'Save',
  显示: 'Display',
  共享词条: 'Shared term',
  词条: 'Term',
  连接测试: 'Connection test',
  人工操作: 'Manual action',
  补齐: 'Fill',
  语言切换: 'Language switch',
  明确重译: 'Retranslate',
  词典: 'Dictionary',
  编辑规则: 'Edit rule',
  分类词条: 'Genre term',
  原词: 'Original term',
  语言: 'Language',
  译词: 'Translation',
  恢复此语言内置词: 'Restore builtin for this language',
  补齐此词译文: 'Fill translation for this term',
  原名: 'Original name',
  中文名: 'Chinese name',
  英文名: 'English name',
  '别名（每行一个）': 'Aliases (one per line)',
  删除人名规则: 'Delete person rule',
  编辑: 'Edit',
  '版本已保存。显示保持当前状态。': 'Version saved; current display preserved.',
  '操作已保存，显示会由后台更新。': 'Action saved; the background executor will update display.',
  '来源绑定已保存。': 'Source binding saved.',
  '设置已保存；语言切换会进入同一处理流程。':
    'Settings saved; language changes enter the same processing flow.',
  '请先保存或放弃连接设置草稿。': 'Save or discard the connection draft first.',
  '翻译固定示例词「接続確認」，消耗一次模型请求。结果保留在词典，不改影片。':
    'Translate the fixed example word “接続確認” using one model request. Keep the result in the dictionary; no movie changes.',
  确认发送一次测试请求: 'Send one test request',
  接受可能重复计费并重试: 'Accept possible duplicate charges and retry',
  取消此批: 'Cancel this batch',
  '需要明确接受可能重复计费的风险。': 'Explicitly accept the possible duplicate-charge risk.',
  新增影片: 'New movies',
  原文变化: 'Source changes',
  处理中断恢复: 'Recover interrupted work',
  执行异常: 'Execution error',
  '历史失败/暂停': 'Historical failed/paused',
  '失败/暂停为批次历史记录；当前待处理项见下方问题列表。':
    'Failed/paused counts are batch history; see the issues below for items needing attention now.',
  空原文: 'Empty original',
};
export const uiWords = {
  ...interfaceWords,
  ...Object.fromEntries(Object.values(messages).map((text) => [text.zh, text.en])),
};
export const uiKeys = Object.keys(uiWords).sort((a, b) => b.length - a.length);
export const uiPattern = new RegExp(
  uiKeys.map((x) => x.replace(/[.*+?^$\x7b}()|[\]\\]/g, '\\$&')).join('|'),
  'g',
);
const uiOriginals = new WeakMap(),
  uiAttributes = new WeakMap();
export function paintUi(root = page) {
  const paintText = (node) => {
    const parent = node.parentElement;
    if (
      !parent ||
      parent.closest(
        'style,script,textarea,input,#luUiLanguage option,#luModel option:not([value=""]),#luBrowseLibrary option:not([value=""]),.lu-read,.lu-data,pre,#luMovieList .lu-grow,#luTermList .lu-grow,#luPeopleList .lu-grow,#luEditorTitle,#luHistorySource',
      )
    )
      return;
    let saved = uiOriginals.get(node);
    if (!saved || (node.nodeValue !== saved.original && node.nodeValue !== saved.rendered))
      saved = { original: node.nodeValue };
    const rendered =
      uiLanguage === 'en'
        ? saved.original
            .replace(uiPattern, (x) => uiWords[x])
            .replace(/(\d+) 部(?=\s|$)/g, '$1 movies')
        : saved.original;
    saved.rendered = rendered;
    uiOriginals.set(node, saved);
    if (node.nodeValue !== rendered) node.nodeValue = rendered;
  };
  if (root.nodeType === 3) paintText(root);
  else {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode())) paintText(node);
  }
  const selector = '[aria-label],[placeholder]';
  const elements = root.querySelectorAll ? [...root.querySelectorAll(selector)] : [];
  if (root.matches?.(selector)) elements.unshift(root);
  for (const el of elements) {
    const records = uiAttributes.get(el) || {};
    for (const key of ['aria-label', 'placeholder']) {
      if (!el.hasAttribute(key)) continue;
      const actual = el.getAttribute(key);
      let saved = records[key];
      if (!saved || (actual !== saved.original && actual !== saved.rendered))
        saved = { original: actual };
      saved.rendered =
        uiLanguage === 'en' ? saved.original.replace(uiPattern, (x) => uiWords[x]) : saved.original;
      records[key] = saved;
      if (actual !== saved.rendered) el.setAttribute(key, saved.rendered);
    }
    uiAttributes.set(el, records);
  }
}

export function queueUiPaint(records) {
  const pending = queueUiPaint.pending || (queueUiPaint.pending = new Set());
  for (const record of records) {
    if (record.type === 'characterData') pending.add(record.target);
    else for (const node of record.addedNodes) pending.add(node);
  }
  if (!pending.size || queueUiPaint.scheduled) return;
  queueUiPaint.scheduled = true;
  queueMicrotask(() => {
    queueUiPaint.scheduled = false;
    const roots = [...pending].filter((node) => page.contains(node));
    pending.clear();
    for (const root of roots) {
      if (!roots.some((other) => other !== root && other.contains(root))) paintUi(root);
    }
  });
}
