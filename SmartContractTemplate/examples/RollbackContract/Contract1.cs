using System;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;

namespace RollbackContract
{
    /// <summary>
    /// 回款合约：可以向本合约任意转入neo、gas，但从本合约转出时每个utxo只能原路返还
    /// 注意:退款（由本合约向外转出）时为了方便校验，只支持一对一退款
    /// </summary>
    class Contract1 : SmartContract
    {
        public static bool Main(string methond,object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                //本次转出交易
                var tx = ExecutionEngine.ScriptContainer as Transaction;
                //本合约地址
                var currentScriptHash = ExecutionEngine.ExecutingScriptHash;
                var inputs = tx.GetInputs();
                var inputDetails = tx.GetReferences();
                var outputs = tx.GetOutputs();
                if (inputs.Length != 1 || inputDetails.Length != 1 || outputs.Length != 1)
                {
                    //只准一对一转出
                    Runtime.Log("Only support 1 to 1 transfer");
                    return false;
                }
                //查询原始输入地址，只准向原始输入地址回转钱
                //查询input指向的转入交易信息
                var inputTx = Blockchain.GetTransaction(inputs[0].PrevHash);
                var recordAddress = inputTx.GetReferences()[0].ScriptHash;//上笔转入交易的input地址即本次要退款的地址，以第一个input地址为准

                if (inputDetails[0].ScriptHash != currentScriptHash)
                {
                    //上笔交易的输出（本次的输入）必须是本合约地址
                    Runtime.Log("Input receiver must be this contract");
                    return false;
                }

                if (inputDetails[0].Value != outputs[0].Value)
                {
                    //输入输出金额必须一致
                    Runtime.Log("Input amount must be equal to Output amount");
                    return false;
                }
                if (recordAddress != outputs[0].ScriptHash)
                {
                    //必须向入账地址退款
                    Runtime.Log("Output address must be the address which send asset to this contract");
                    return false;
                }
                Runtime.Log("ok");
                //Runtime.CheckWitness(recordAddress);//可以在此处校验签名，默认不校验，即默认任何人可以代替别人发起退款
                return true;
            }
            return true;
        }
    }
}
