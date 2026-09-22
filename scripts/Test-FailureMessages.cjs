const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8');
const script=html.match(/<script>([\s\S]*?)<\/script>/)[1];
new vm.Script(script);
const fragment=script.slice(script.indexOf('        const failureKinds ='),script.indexOf('        const itemStates ='));
const ctx=vm.createContext({workbenchReasons:{}});
vm.runInContext(script.match(/Object.assign\(workbenchReasons, \{\s*Authentication:[\s\S]*?\n        \}\);/)[0],ctx);
vm.runInContext(fragment,ctx);
const explanation=code=>vm.runInContext(`failureText(${JSON.stringify(code)})`,ctx);
const checks=[];
function check(name,ok){assert(ok,name);checks.push({name,passed:true});}
check('placeholder errors distinguish failed validation from refusal',/Name markers/.test(explanation('PlaceholderMismatch'))&&/not evidence of refusal/.test(explanation('PlaceholderMismatch')));
check('refusal explains detection without inventing provider reason',/refusal.*filtering/.test(explanation('Refused'))&&/specific provider rule is unknown/.test(explanation('Refused')));
check('quota rate authentication configuration and transport are distinct',new Set(['QuotaExceeded','RateLimited','Authentication','Configuration','TransportUncertain'].map(explanation)).size===5);
check('unconfigured model offers settings and model list guidance',/Choose a model in Settings/.test(explanation('model_not_configured')) && /Connection tests/.test(explanation('model_not_configured')));
check('unknown failure keeps diagnostic code',explanation('FutureSyntheticError').includes('FutureSyntheticError'));
check('invalid output types offer manual correction',['PlaceholderMismatch','NameCountMismatch','TruncatedOrIncomplete','InvalidStructure','InvalidResponse','InvalidText'].every(x=>explanation(x).includes('manually')));
check('uncertain outcome does not promise safe resend',explanation('TransportUncertain').includes('avoid duplicate requests'));


console.log(JSON.stringify({passed:checks.length,failed:0}));
