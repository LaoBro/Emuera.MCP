using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.GameView;

namespace MinorShift.Emuera.Runtime.Script.Statements.Variable;

internal interface IVariableEvaluator
{
	// Properties
	VariableData VariableData { get; }
	long RESULT { get; set; }
	string RESULTS { get; set; }
	long[] RESULT_ARRAY { get; }
	string[] RESULTS_ARRAY { get; }
	long SELECTCOM { get; set; }
	long[] SELECTCOM_ARRAY { get; }
	long NEXTCOM { get; set; }
	string SAVEDATA_TEXT { get; set; }
	string[] ITEMNAME { get; }
	long[] ITEMSALES { get; }
	long[] ITEMPRICE { get; }
	long CHARANUM { get; }
	long COUNT { get; set; }
	long TARGET { get; set; }
	long MASTER { get; set; }
	long ASSI { get; set; }
	long ASSIPLAY { set; }
	long PREVCOM { get; set; }

	// Random
	long GetNextRand(long max);
	void Randomize(long seed);
	void InitRanddata();
	void DumpRanddata();

	// Character management
	void ResetData();
	void AddCharacterFromCsvNo(long no);
	void DelAllCharacter();
	void PickUpChara(long[] noList);
	string GetCharacterDataString(long target, FunctionCode func);
	string GetCharacterParamString(long target, int paramCode);
	string GetHavingItemsString();

	// Game loop callbacks
	void UpdateInBeginTrain();
	void UpdateAfterShowUsercom();
	void UpdateAfterInputCom();
	void UpdateAfterSourceCheck();
	void UpdateInUpcheck(EmueraConsole window, bool skipPrint);
	void CUpdateInUpcheck(EmueraConsole window, long target, bool skipPrint);

	// Save/Load
	bool SaveTo(int index, string text);
	EraDataResult CheckData(int saveIndex, EraSaveFileType type);
	bool LoadFrom(int index);
	void VarSize(VariableToken varID);
	void SetDefaultStain(long no);
	void SetEncodingResult(int[] ary);
	void IamaMunchkin();

	// Items
	bool ItemSales(long index);
	bool BuyItem(long index);
}
