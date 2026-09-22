const fs=require('fs'),vm=require('vm'),assert=require('assert');
const html=fs.readFileSync('src/Localizer.Plugin/Web/admin.html','utf8'),script=html.match(/<script>([\s\S]*?)<\/script>/)[1];new vm.Script(script);
const storage=new Map();const ctx=vm.createContext({sessionStorage:{getItem:k=>storage.get(k)||null,setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},genreTaskMessage:()=>{}});
vm.runInContext(script.slice(script.indexOf('        let genreTaskDrafts'),script.indexOf('        function genreTaskMessage')),ctx);
let passed=0;const run=s=>vm.runInContext(s,ctx);function check(ok){assert(ok);passed++;}
run("genreTaskDrafts.set(0,'Unsaved');persistGenreDraft('libA','taskA',3,0)");
check(run("readGenreDraft('libA','taskA').edits[0][1]")==='Unsaved');
check(run("readGenreDraft('libB','taskA')")===null);
check(run("readGenreDraft('libA','taskB')")===null);
check(run("readGenreDraft('libA','taskA').revision")===3);
run("genreTaskDrafts.delete(0);persistGenreDraft('libA','taskA',4,0)");check(storage.size===0);
storage.set('jml-genre-draft-v1:libA:taskA','{bad');check(run("readGenreDraft('libA','taskA')")===null);
storage.set('jml-genre-draft-v1:libA:taskA',JSON.stringify({revision:1,start:0,edits:[[999,'wrong page']]}));check(run("readGenreDraft('libA','taskA')")===null);
check(script.includes('recovered.revision!==task.Revision'));
check(script.includes("x.dataset.genreDisabled === 'true'"));
console.log(JSON.stringify({passed,failed:0}));
