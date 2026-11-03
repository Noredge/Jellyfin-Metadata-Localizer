const page = document.querySelector('#localizerUnified');

const $ = (s) => page.querySelector(s);

const $$ = (s) => Array.from(page.querySelectorAll(s));

let state = null,
  connection = null,
  active = 'overview',
  offset = 0,
  total = 0,
  listAbort = null,
  detailAbort = null,
  timer = null,
  dirty = false,
  connectionDirty = false,
  preview = null,
  workBatch = null,
  workTarget = null,
  workOffset = 0,
  editor = null,
  fieldName = 'Name',
  drafts = {},
  historyOffset = 0,
  historySource = null,
  termOffset = 0,
  personOffset = 0,
  issueOffset = 0,
  issueKind = null,
  issueCode = null,
  rule = null,
  settingsLoading = false,
  fillLoading = false;

let polling = false,
  pollAbort = null,
  pollEpoch = 0,
  pageVisible = true,
  statusRevision = 0,
  startEpoch = 0;

const selected = new Set(),
  statusOwners = new Map();

let searchTimer;

let uiLanguage = localStorage.getItem('metadata-localizer-ui-language') || 'zh-Hans';
