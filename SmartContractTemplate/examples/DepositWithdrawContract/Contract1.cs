using System;
using System.ComponentModel;
using System.Numerics;
using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;

namespace DepositWithdrawContract
{
    /// <summary>
    /// 充提合约:可以向本合约充值neo，提取时只能取出不超过自己充值金额的neo
    /// 注意：禁止往本合约充值gas
    /// </summary>
    class Contract1 : SmartContract
    {
        /// <summary>
        /// Neo Asset id little endian
        /// </summary>
        private static readonly byte[] NeoAssetId = "9b7cffdaa674beae0f930ebe6085af9093e5fe56b34a5c220ccdcf6efc336fc5".HexToBytes();

        /// <summary>
        /// 保存 user=>balance 键值对
        /// </summary>
        private const string AssetMap = "asset";

        /// <summary>
        /// 保存准备取钱的utxo：TxId => user
        /// 约定:TxId中的第一个output即为取钱交易的input
        /// 注意：在这里保存的utxo将只能用于“取钱”，用作普通转账的input将会验证失败
        /// </summary>
        private const string WithdrawRecords = "withdrawRecords";



        public static object Main(string method, object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                var tx = ExecutionEngine.ScriptContainer as Transaction;
                var inputs = tx.GetInputs();
                var outputs = tx.GetOutputs();
                //Check if the input has been marked
                foreach (var input in inputs)
                {
                    if (input.PrevIndex == 0)
                    {
                        // 按照约定只有index为0的 UTXO 可用作取款
                        var withdrawRecords = Storage.CurrentContext.CreateMap(WithdrawRecords);
                        var withdrawUser = withdrawRecords.Get(input.PrevHash); //0.1
                        // 有值代表当前utxo已经被标记为“待取款”
                        if (withdrawUser.Length > 0)
                        {
                            //对于待取款的交易，只允许包含一个输入和一个输出
                            if (inputs.Length != 1 || outputs.Length != 1)
                                return false;

                            //output必须是取款人的地址
                            return outputs[0].ScriptHash == withdrawUser;
                        }
                    }
                }
                var currentHash = ExecutionEngine.ExecutingScriptHash;
                //非取款交易，只允许合约向自己转账neo，即调用withdraw方法时进行的UTXO “重组”操作
                BigInteger inputAmount = 0;
                foreach (var refe in tx.GetReferences())
                {
                    if (refe.AssetId != NeoAssetId)
                        return false;//Not allowed to operate assets other than NEO

                    if (refe.ScriptHash == currentHash)
                        inputAmount += refe.Value;
                }
                //Check that there is no money left this contract
                BigInteger outputAmount = 0;
                foreach (var output in outputs)
                {
                    if (output.ScriptHash == currentHash)
                        outputAmount += output.Value;
                }
                return outputAmount == inputAmount;
            }
            if (Runtime.Trigger == TriggerType.Application)
            {
                if (method == "balanceOf") return BalanceOf((byte[])args[0]);

                if (method == "deposit") return Deposit();

                if (method == "getWithdrawTarget") return GetWithdrawTarget((byte[])args[0]);

                if (method == "withdraw") return Withdraw((byte[])args[0]);

            }
            return false;
        }


        /// <summary>
        /// 存钱（user => sc）
        /// 注意：所有存钱都必须走此合约方法，发起普通交易存钱将无法追踪用户余额，变成死账
        /// </summary>
        /// <returns></returns>
        [DisplayName("deposit")]
        public static object Deposit()
        {
            //获取当前交易信息
            var tx = ExecutionEngine.ScriptContainer as Transaction;
            //获取本合约地址hash
            var currentScriptHash = ExecutionEngine.ExecutingScriptHash;

            byte[] sender = null;
            var inputDetails = tx.GetReferences();
            foreach (var inputDetail in inputDetails)
            {
                if (inputDetail.AssetId == NeoAssetId)
                {
                    sender = sender ?? inputDetail.ScriptHash;
                }
                //存钱中不允许合约向自己转账
                if (inputDetail.ScriptHash == currentScriptHash)
                {
                    return false;
                }
            }

            // 转入的neo总金额
            var outputs = tx.GetOutputs();
            ulong value = 0;
            foreach (var output in outputs)
            {
                if (output.ScriptHash == currentScriptHash && output.AssetId == NeoAssetId)
                {
                    value += (ulong)output.Value;
                }
            }

            //记录sender的已转入金额
            var assetMap = Storage.CurrentContext.CreateMap(AssetMap);
            var amount = assetMap.Get(sender).AsBigInteger(); //0.1

            assetMap.Put(sender, amount + value); //1

            return true;
        }

        /// <summary>
        /// 取钱(sc => sc)
        /// 注意：本次交易实际是utxo的重组，neo由自己转向自己，但会产生一个可以直接向用户打钱的utxo
        /// </summary>
        /// <param name="receiverScriptHash">取钱人公钥hash</param>
        /// <returns></returns>
        [DisplayName("withdraw")]
        public static object Withdraw(byte[] receiverScriptHash)
        {
            if (receiverScriptHash.Length != 20)
                throw new InvalidOperationException("The parameter receiverScriptHash SHOULD be 20-byte addresses.");
            var tx = ExecutionEngine.ScriptContainer as Transaction;
            //output[0] 约定为要取出的金额
            var preWithdraw = tx.GetOutputs()[0];
            // 只能取neo
            if (preWithdraw.AssetId != NeoAssetId) return false;

            //只能由合约转给自己
            if (preWithdraw.ScriptHash != ExecutionEngine.ExecutingScriptHash) return false;

            //防止双花
            var withdrawRecords = Storage.CurrentContext.CreateMap(WithdrawRecords);
            if (withdrawRecords.Get(tx.Hash).Length > 0) return false; //0.1

            //需要取款者的签名
            if (!Runtime.CheckWitness(receiverScriptHash)) return false; //0.2

            //Reduce the balance of the refund person
            StorageMap asset = Storage.CurrentContext.CreateMap(AssetMap);
            var balance = asset.Get(receiverScriptHash).AsBigInteger(); //0.1
            var preRefundValue = preWithdraw.Value;
            if (balance < preRefundValue)
            {
                return false;//余额不足
            }
            if (balance == preRefundValue)
                asset.Delete(receiverScriptHash); //0.1
            else
                asset.Put(receiverScriptHash, balance - preRefundValue); //1

            //将取钱者和本次交易id保存起来，用作下次取款交易的verificaton验证
            withdrawRecords.Put(tx.Hash, receiverScriptHash); //1
            return true;
        }


        [DisplayName("balanceOf")]
        public static BigInteger BalanceOf(byte[] account)
        {
            if (account.Length != 20)
                throw new InvalidOperationException("The parameter account SHOULD be 20-byte addresses.");
            StorageMap asset = Storage.CurrentContext.CreateMap(AssetMap);
            return asset.Get(account).AsBigInteger(); //0.1
        }


        [DisplayName("getWithdrawTarget")]
        public static byte[] GetWithdrawTarget(byte[] txId)
        {
            if (txId.Length != 32)
                throw new InvalidOperationException("The parameter txId SHOULD be 32-byte transaction hash.");
            StorageMap withdraw = Storage.CurrentContext.CreateMap(WithdrawRecords);
            return withdraw.Get(txId); //0.1
        }
    }
}
