$$('[data-page]').forEach((el) => (el.onclick = () => show(el.dataset.page)));

$('#luRefresh').onclick = () =>
  run(async () => {
    await loadState();
    await loadOverview();
  });

$('#luBrowseHost').onclick = () => {
  window.location.href = '#/';
};

$('#luInitialize').onclick = () =>
  run(async () => {
    await api('initialize', {});
    await loadState();
    show('settings');
  });

$('#luFillAll').onclick = () => run(() => makePreview(null));

$('#luFillSelected').onclick = () => run(() => makePreview([...selected]));

$('#luForceSelected').onclick = () => run(() => makePreview([...selected], true));

$('#luClosePreview').onclick = () => $('#luPreview').close();

$('#luCommit').onclick = () =>
  run(async () => {
    if (!preview) return;
    $('#luCommit').disabled = true;
    try {
      if (preview.kind === 'language') {
        await saveSettings(true);
        $('#luPreview').close();
        show('overview');
      } else if (preview.kind === 'connection_test') {
        await api('connection/test', {
          settingsRevision: preview.settingsRevision,
          commandId: preview.commandId,
          originals: [],
        });
        $('#luPreview').close();
        await openWork(null);
      } else {
        await api('submit', { previewId: preview.id, commandId: preview.commandId });
        $('#luPreview').close();
        show('overview');
      }
    } finally {
      $('#luCommit').disabled = false;
    }
  }, '#luPreviewStatus');

$('#luAllWork').onclick = () => run(() => openWork(null));

$('#luCloseWork').onclick = () => {
  cancelRead('work');
  workView().consents.clear();
  $('#luWorkDialog').close();
};

$('#luWorkDialog').addEventListener('cancel', () => {
  cancelRead('work');
  workView().consents.clear();
});

$('#luWorkPrev').onclick = () => {
  workOffset = Math.max(0, workOffset - 30);
  run(loadWork, '#luWorkStatus');
};

$('#luWorkNext').onclick = () => {
  workOffset += 30;
  run(loadWork, '#luWorkStatus');
};

$('#luPrev').onclick = () => {
  offset = Math.max(0, offset - 30);
  run(loadMovies);
};

$('#luNext').onclick = () => {
  offset += 30;
  run(loadMovies);
};

$('#luSearch').oninput = () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => {
    offset = 0;
    run(loadMovies);
  }, 220);
};

['#luFilter', '#luBrowseLibrary'].forEach(
  (id) =>
    ($(id).onchange = () => {
      offset = 0;
      selected.clear();
      run(loadMovies);
    }),
);

$('#luSave').onclick = () => run(() => saveSettings(false));

$('#luDiscard').onclick = () => {
  dirty = false;
  connectionDirty = false;
  run(loadSettings);
};

$('#luSettings').addEventListener('input', (e) => {
  if (e.target.closest('#luTermsDetails,#luPeopleDetails')) return;
  settingsDirty();
  if (e.target.closest('.lu-card')?.contains($('#luEndpoint'))) connectionDirty = true;
});

$('#luCloseEditor').onclick = () => {
  if (canCloseEditor()) $('#luEditor').close();
};

$('#luDiscardDraft').onclick = () => {
  drafts = {};
  $('#luEditor').close();
};

$('#luEditor').addEventListener('cancel', (e) => {
  if (!canCloseEditor()) e.preventDefault();
});

$('#luEditor').addEventListener('input', (e) => {
  if (e.target.id === 'luDraft') {
    e.target.dataset.changed = 'true';
    saveDraft();
  }
});

$('#luCloseRule').onclick = () => $('#luRuleDialog').close();

$('#luSaveRule').onclick = () => run(() => saveRule(false), '#luRuleStatus');

$('#luDeleteRule').onclick = () => run(() => saveRule(true), '#luRuleStatus');

$('#luTermsDetails').addEventListener('toggle', () => {
  if ($('#luTermsDetails').open) run(loadTerms);
});

$('#luPeopleDetails').addEventListener('toggle', () => {
  if ($('#luPeopleDetails').open) run(loadPeople);
});

$('#luTermFind').onclick = () => {
  termOffset = 0;
  run(loadTerms);
};

$('#luTermPrev').onclick = () => {
  termOffset = Math.max(0, termOffset - 30);
  run(loadTerms);
};

$('#luTermNext').onclick = () => {
  termOffset += 30;
  run(loadTerms);
};

$('#luAddTerm').onclick = () => editTerm(null);

$('#luAddPerson').onclick = () => editPerson(null);

$('#luPersonFind').onclick = () => {
  personOffset = 0;
  run(loadPeople);
};

$('#luPersonSearch').onkeydown = (e) => {
  if (e.key === 'Enter') {
    personOffset = 0;
    run(loadPeople);
  }
};

$('#luPersonPrev').onclick = () => {
  personOffset = Math.max(0, personOffset - 30);
  run(loadPeople);
};

$('#luPersonNext').onclick = () => {
  personOffset += 30;
  run(loadPeople);
};

$('#luCloseIssues').onclick = () => $('#luIssueDialog').close();

$('#luIssuePrev').onclick = () => {
  issueOffset = Math.max(0, issueOffset - 30);
  run(loadIssues, '#luIssueStatus');
};

$('#luIssueNext').onclick = () => {
  issueOffset += 30;
  run(loadIssues, '#luIssueStatus');
};

page.addEventListener('click', (e) => {
  const el = e.target.closest('button');
  if (!el) return;
  if (el.dataset.issueKind)
    run(() => openIssues(el.dataset.issueKind, el.dataset.issueCode), '#luIssueStatus');
  if (el.dataset.issueWork)
    run(async () => {
      await openWork(null, el.dataset.issueWork);
      $('#luIssueDialog').close();
    }, '#luIssueStatus');
  if (el.dataset.issueMovie)
    run(async () => {
      await openMovie(el.dataset.issueMovie, el.dataset.issueField);
      if (editor?.id === el.dataset.issueMovie) $('#luIssueDialog').close();
    }, '#luIssueStatus');
  if (el.dataset.movie) run(() => openMovie(el.dataset.movie));
  if (el.dataset.batch) run(() => openWork(el.dataset.batch));
  if (el.dataset.cancel)
    run(async () => {
      await api('cancel/' + el.dataset.cancel, {});
      await loadOverview();
    });
  if (el.dataset.field) {
    saveDraft();
    fieldName = el.dataset.field;
    historySource = null;
    historyOffset = 0;
    renderEditor();
  }
  if (el.dataset.action) run(() => fieldAction(el.dataset.action), '#luEditorStatus');
  if (el.dataset.choose) run(() => fieldAction('choose', el.dataset.choose), '#luEditorStatus');
  if (el.id === 'luRetranslateField')
    run(async () => {
      const data = await api('preview', {
        settingsRevision: editor.settings.revision,
        mediaIds: [editor.id],
        fields: [fieldName],
        force: true,
      });
      preview = { ...data, commandId: uid() };
      $('#luPreviewText').textContent =
        '明确重译此影片的' + labels[fieldName] + '。本次会重新请求模型。';
      $('#luCommit').disabled = !data.count;
      $('#luPreview').showModal();
    }, '#luEditorStatus');
  if (el.dataset.retry) run(() => retryWithConsent(el.dataset.retry), '#luWorkStatus');
  if (el.dataset.consent)
    run(
      () => retryWithConsent(el.dataset.consent, true, el.dataset.consentVersion),
      '#luWorkStatus',
    );
  if (el.dataset.cancelConsent)
    run(() => cancelWorkConsent(el.dataset.cancelConsent), '#luWorkStatus');
});

page.addEventListener('change', (e) => {
  if (e.target.dataset.select) {
    if (e.target.checked) selected.add(e.target.dataset.select);
    else selected.delete(e.target.dataset.select);
    $('#luMovieCount').textContent = total + ' 部影片 · 已选 ' + selected.size + ' 部';
  }
});

$('#luListModels').onclick = () => run(listModels);

$('#luTestConnection').onclick = () =>
  run(async () => {
    if (dirty) throw new Error('请先保存或放弃连接设置草稿。');
    await loadState();
    preview = {
      kind: 'connection_test',
      settingsRevision: state.settings.revision,
      commandId: uid(),
    };
    $('#luPreviewText').textContent =
      '翻译固定示例词「接続確認」，消耗一次模型请求。结果保留在词典，不改影片。';
    $('#luCommit').textContent = '确认发送一次测试请求';
    $('#luCommit').disabled = false;
    $('#luPreview').showModal();
  });

$('#luUiLanguage').value = uiLanguage;

new MutationObserver(paintUi).observe(page, {
  subtree: true,
  childList: true,
  characterData: true,
});

$('#luUiLanguage').onchange = () => {
  uiLanguage = $('#luUiLanguage').value;
  localStorage.setItem('metadata-localizer-ui-language', uiLanguage);
  paintUi();
};

paintUi();

page.addEventListener('viewshow', () => run(start));

document.addEventListener('visibilitychange', () => {
  invalidatePoll();
  if (!document.hidden && pageVisible) poll();
});

page.addEventListener('viewhide', () => {
  pageVisible = false;
  invalidatePoll();
  cancelRead();
  clearInterval(timer);
  listAbort?.abort();
  detailAbort?.abort();
});

window.addEventListener('beforeunload', (e) => {
  saveDraft();
  if (dirty || Object.values(drafts).some((x) => x.changed)) {
    e.preventDefault();
    e.returnValue = '';
  }
});

run(start);
